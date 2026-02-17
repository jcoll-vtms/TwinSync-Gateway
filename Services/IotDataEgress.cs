using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class IotDataEgress : IAsyncDisposable
    {
        private readonly IotMqttConnection _mqtt;

        private readonly object _gate = new();

        // Publish allow-list + last-known frame cache
        private readonly HashSet<DeviceKey> _enabled = new();
        private readonly Dictionary<DeviceKey, IDeviceFrame> _latest = new();

        private CancellationTokenSource? _cts;
        private Task? _pumpTask;

        // MQTT publish sequence (debug)
        private long _seq;

        public event Action<string>? Log;

        public IotDataEgress(IotMqttConnection mqtt, string tenantId, string gatewayId)
        {
            // tenantId/gatewayId kept for compatibility, but we publish using DeviceKey values
            _mqtt = mqtt;
        }

        /// <summary>
        /// Enables/disables publishing for a device.
        /// REQUIRED INVARIANT: allowed=false MUST clear cached latest frame.
        /// </summary>
        public void SetPublishAllowed(DeviceKey key, bool allowed)
        {
            lock (_gate)
            {
                if (!allowed)
                {
                    _enabled.Remove(key);
                    _latest.Remove(key); // ✅ critical: prevents stale republish forever
                    return;
                }

                _enabled.Add(key);
            }
        }

        /// <summary>
        /// Hard-remove a device from egress state (alias for disable+clear).
        /// </summary>
        public void ClearDevice(DeviceKey key)
        {
            lock (_gate)
            {
                _enabled.Remove(key);
                _latest.Remove(key);
            }
        }

        /// <summary>
        /// Clear all cached frames and disable all devices.
        /// </summary>
        public void ClearAll()
        {
            lock (_gate)
            {
                _enabled.Clear();
                _latest.Clear();
            }
        }

        public void Start(TimeSpan publishPeriod)
        {
            if (publishPeriod <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(publishPeriod));

            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpAsync(publishPeriod, _cts.Token));
        }

        public async Task StopAsync()
        {
            var cts = _cts;
            var task = _pumpTask;

            _cts = null;
            _pumpTask = null;

            try { cts?.Cancel(); } catch { }

            try
            {
                if (task != null)
                    await task.ConfigureAwait(false);
            }
            catch { }
            finally
            {
                try { cts?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Enqueue a new frame. If device is not publish-allowed, ignore it.
        /// This prevents "ghost cache refilling" after disable.
        /// </summary>
        public void Enqueue(DeviceKey key, IDeviceFrame frame)
        {
            if (frame == null) return;

            lock (_gate)
            {
                if (!_enabled.Contains(key))
                    return;

                _latest[key] = frame;
            }
        }

        private async Task PumpAsync(TimeSpan period, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, ct).ConfigureAwait(false);

                    List<(DeviceKey Key, IDeviceFrame Frame)> snapshot;

                    lock (_gate)
                    {
                        snapshot = new List<(DeviceKey, IDeviceFrame)>(_enabled.Count);

                        foreach (var key in _enabled)
                        {
                            if (_latest.TryGetValue(key, out var frame) && frame != null)
                                snapshot.Add((key, frame));
                        }
                    }

                    foreach (var item in snapshot)
                        await PublishOneAsync(item.Key, item.Frame, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log?.Invoke($"Data pump error: {ex.Message}");
                }
            }
        }

        private async Task PublishOneAsync(DeviceKey key, IDeviceFrame frame, CancellationToken ct)
        {
            if (!_mqtt.IsConnected) return;

            // For now: only robot telemetry frames are supported on this topic/payload.
            // PLC "machine data" will become another frame type + payload shape later.
            if (frame is not TelemetryFrame tf)
            {
                // optional: helpful during bring-up
                // Log?.Invoke($"Ignoring unsupported frame type '{frame.GetType().Name}' for {key}");
                return;
            }

            var seq = Interlocked.Increment(ref _seq);

            // Prefer the tenant-scoped topic going forward.
            var tenantTopic = $"twinsync/{key.TenantId}/{key.GatewayId}/telemetry/{key.DeviceId}";

            // Optional legacy topic (remove when you're ready)
            var legacyTopic = $"twinsync/{key.GatewayId}/telemetry/{key.DeviceId}";

            var payload = new TelemetryOut
            {
                seq = seq,
                ts = tf.Timestamp.ToUnixTimeMilliseconds(),
                j = tf.JointsDeg,
                di = tf.DI != null ? ToStringKeyDict(tf.DI) : null,
                gi = tf.GI != null ? ToStringKeyDict(tf.GI) : null,
                go = tf.GO != null ? ToStringKeyDict(tf.GO) : null
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);

            await _mqtt.PublishAsync(
                legacyTopic,
                bytes,
                qos: MqttQualityOfServiceLevel.AtMostOnce,
                retain: false,
                ct).ConfigureAwait(false);

            await _mqtt.PublishAsync(
                tenantTopic,
                bytes,
                qos: MqttQualityOfServiceLevel.AtMostOnce,
                retain: false,
                ct).ConfigureAwait(false);
        }

        private static Dictionary<string, int> ToStringKeyDict(IReadOnlyDictionary<int, int> src)
        {
            var d = new Dictionary<string, int>(src.Count);
            foreach (var kv in src)
                d[kv.Key.ToString()] = kv.Value;
            return d;
        }

        private sealed class TelemetryOut
        {
            public long seq { get; set; }
            public long ts { get; set; }
            public double[]? j { get; set; }
            public Dictionary<string, int>? di { get; set; }
            public Dictionary<string, int>? gi { get; set; }
            public Dictionary<string, int>? go { get; set; }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }
    }
}
