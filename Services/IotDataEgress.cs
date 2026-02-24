// =====================================================
// COMPLETE REPLACEMENT: IotDataEgress.cs
// =====================================================
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
    /// <summary>
    /// Cloud publisher for device frames.
    ///
    /// Critical invariants:
    ///  - Disable publish => clear cached latest frame
    ///  - Enqueue ignores frames unless enabled (prevents ghost cache refilling)
    /// </summary>
    public sealed class IotDataEgress : IAsyncDisposable
    {
        private readonly IotMqttConnection _mqtt;

        private readonly object _gate = new();

        private readonly HashSet<DeviceKey> _enabled = new();
        private readonly Dictionary<DeviceKey, IDeviceFrame> _latest = new();

        private CancellationTokenSource? _cts;
        private Task? _pumpTask;

        private long _publishSeq;

        public event Action<string>? Log;

        public IotDataEgress(IotMqttConnection mqtt, string tenantId, string gatewayId)
        {
            _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
        }

        public void SetPublishAllowed(DeviceKey key, bool allowed)
        {
            lock (_gate)
            {
                if (!allowed)
                {
                    _enabled.Remove(key);
                    _latest.Remove(key);
                    return;
                }

                _enabled.Add(key);
            }
        }

        public void ClearDevice(DeviceKey key)
        {
            lock (_gate)
            {
                _enabled.Remove(key);
                _latest.Remove(key);
            }
        }

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
                    Log?.Invoke($"Data pump error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task PublishOneAsync(DeviceKey key, IDeviceFrame frame, CancellationToken ct)
        {
            if (!_mqtt.IsConnected) return;

            // Canonical multi-device data topic:
            // twinsync/{tenant}/{gateway}/data/{deviceType}/{deviceId}
            var topic = $"twinsync/{key.TenantId}/{key.GatewayId}/data/{key.DeviceType}/{key.DeviceId}";

            var pubSeq = Interlocked.Increment(ref _publishSeq);

            object payload = frame switch
            {
                TelemetryFrame tf => new TelemetryPayload
                {
                    j = tf.JointsDeg,
                    di = tf.DI != null ? ToStringKeyDict(tf.DI) : null,
                    gi = tf.GI != null ? ToStringKeyDict(tf.GI) : null,
                    go = tf.GO != null ? ToStringKeyDict(tf.GO) : null,
                    dO = tf.DO != null ? ToStringKeyDict(tf.DO) : null,
                    r = tf.R != null ? ToStringKeyRegisterDict(tf.R) : null,
                    v = tf.VAR != null ? new Dictionary<string, string>(tf.VAR) : null
                },
                PlcFrame pf => new PlcPayload
                {
                    values = pf.Values != null
                        ? pf.Values.ToDictionary(k => k.Key, v => ToJsonPlcValue(v.Value))
                        : new Dictionary<string, object?>()
                },
                _ => new UnknownPayload
                {
                    type = frame.GetType().Name
                }
            };

            var envelope = new DataEnvelope
            {
                pubSeq = pubSeq,
                ts = frame.Timestamp.ToUnixTimeMilliseconds(),
                frameSeq = frame.Sequence,
                deviceType = key.DeviceType,
                deviceId = key.DeviceId,
                payload = payload
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);

            await _mqtt.PublishAsync(
                topic,
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

        private static Dictionary<string, object> ToStringKeyRegisterDict(IReadOnlyDictionary<int, TelemetryRegisterValue> src)
        {
            // JSON-friendly: { "1": { "i": 123, "r": 123.0 }, ... }
            var d = new Dictionary<string, object>(src.Count);
            foreach (var kv in src)
            {
                d[kv.Key.ToString()] = new { i = kv.Value.IntVal, r = kv.Value.RealVal };
            }
            return d;
        }

        private static object? ToJsonPlcValue(PlcValue v)
        {
            // JSON-friendly: { k: "...", v: ... }
            return v.Kind switch
            {
                PlcValueKind.Null => new { k = "Null", v = (object?)null },

                PlcValueKind.Bool => new { k = "Bool", v = v.Value is bool b ? b : Convert.ToBoolean(v.Value) },

                PlcValueKind.Int32 => new { k = "Int32", v = v.Value == null ? 0 : Convert.ToInt32(v.Value) },
                PlcValueKind.Int64 => new { k = "Int64", v = v.Value == null ? 0L : Convert.ToInt64(v.Value) },

                PlcValueKind.Float => new { k = "Float", v = v.Value == null ? 0f : Convert.ToSingle(v.Value) },
                PlcValueKind.Double => new { k = "Double", v = v.Value == null ? 0d : Convert.ToDouble(v.Value) },

                PlcValueKind.String => new { k = "String", v = v.Value?.ToString() },

                PlcValueKind.Bytes => new { k = "Bytes", v = v.Value is byte[] b ? Convert.ToBase64String(b) : null },

                PlcValueKind.Array => new
                {
                    k = "Array",
                    v = v.Value is PlcValue[] arr ? arr.Select(ToJsonPlcValue).ToArray() : Array.Empty<object?>()
                },

                PlcValueKind.Struct => new
                {
                    k = "Struct",
                    v = v.Value is Dictionary<string, PlcValue> dict
                        ? dict.ToDictionary(kv => kv.Key, kv => ToJsonPlcValue(kv.Value))
                        : new Dictionary<string, object?>()
                },

                _ => new { k = "Unknown", v = v.Value }
            };
        }

        private sealed class DataEnvelope
        {
            public long pubSeq { get; set; }
            public long ts { get; set; }
            public long frameSeq { get; set; }
            public string? deviceType { get; set; }
            public string? deviceId { get; set; }
            public object? payload { get; set; }
        }

        private sealed class TelemetryPayload
        {
            public double[]? j { get; set; }
            public Dictionary<string, int>? di { get; set; }
            public Dictionary<string, int>? gi { get; set; }
            public Dictionary<string, int>? go { get; set; }
            public Dictionary<string, int>? dO { get; set; }
            public Dictionary<string, object>? r { get; set; }
            public Dictionary<string, string>? v { get; set; }
        }

        private sealed class PlcPayload
        {
            public Dictionary<string, object?> values { get; set; } = new();
        }

        private sealed class UnknownPayload
        {
            public string? type { get; set; }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }
    }
}
