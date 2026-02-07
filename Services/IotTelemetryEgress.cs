using MQTTnet.Protocol;
using System.Text.Json;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class IotTelemetryEgress : IAsyncDisposable
    {
        private readonly string _gatewayId;
        private readonly IotMqttConnection _mqtt;

        private readonly object _lock = new();
        private readonly Dictionary<string, TelemetryFrame> _latestByRobot = new();
        private readonly HashSet<string> _publishEnabled = new(StringComparer.Ordinal);

        private CancellationTokenSource? _cts;
        private Task? _pumpTask;

        // Sequence number for telemetry frames (if needed in the future)
        private long _seq;

        public event Action<string>? Log;

        public IotTelemetryEgress(IotMqttConnection mqtt, string gatewayId)
        {
            _mqtt = mqtt;
            _gatewayId = gatewayId;
        }

        // Enable/disable publishing for a robot (used for "only publish when users exist")
        public void SetPublishEnabled(string robotName, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(robotName)) return;
            lock (_lock)
            {
                if (enabled) _publishEnabled.Add(robotName);
                else
                {
                    _publishEnabled.Remove(robotName);
                    _latestByRobot.Remove(robotName); // also drop cached frame so it can't be republished
                }
            }
        }

        // Clear the latest telemetry for a robot (e.g., when it disconnects)
        public void ClearRobot(string robotName)
        {
            if (string.IsNullOrWhiteSpace(robotName)) return;
            lock (_lock)
            {
                _latestByRobot.Remove(robotName);
                _publishEnabled.Remove(robotName);
            }
        }

        // Clear all telemetry data (e.g., on shutdown)
        public void ClearAll()
        {
            lock (_lock)
            {
                _latestByRobot.Clear();
                _publishEnabled.Clear();
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
                if (task != null) await task.ConfigureAwait(false);
            }
            catch { }
            finally
            {
                try { cts?.Dispose(); } catch { }
            }
        }

        public void Enqueue(string robotName, TelemetryFrame frame)
        {
            if (string.IsNullOrWhiteSpace(robotName)) return;
            lock (_lock) _latestByRobot[robotName] = frame;
        }

        private async Task PumpAsync(TimeSpan period, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, ct).ConfigureAwait(false);

                    Dictionary<string, TelemetryFrame> snapshot;
                    lock (_lock)
                    {
                        // publish only for robots that are currently enabled
                        snapshot = new Dictionary<string, TelemetryFrame>(_publishEnabled.Count, StringComparer.Ordinal);
                        foreach (var robot in _publishEnabled)
                        {
                            if (_latestByRobot.TryGetValue(robot, out var frame))
                                snapshot[robot] = frame;
                        }
                    }

                    foreach (var kvp in snapshot)
                        await PublishOneAsync(kvp.Key, kvp.Value, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log?.Invoke($"Telemetry pump error: {ex.Message}");
                }
            }
        }

        private async Task PublishOneAsync(string robotName, TelemetryFrame frame, CancellationToken ct)
        {
            if (!_mqtt.IsConnected) return;

            var seq = Interlocked.Increment(ref _seq); // temp debug

            var topic = $"twinsync/{_gatewayId}/telemetry/{robotName}";
            var payload = new TelemetryOut
            {
                seq = seq, // temp debug
                ts = frame.Timestamp.ToUnixTimeMilliseconds(),
                j = frame.JointsDeg,
                di = frame.DI != null ? ToStringKeyDict(frame.DI) : null,
                gi = frame.GI != null ? ToStringKeyDict(frame.GI) : null,
                go = frame.GO != null ? ToStringKeyDict(frame.GO) : null
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);

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
            foreach (var kv in src) d[kv.Key.ToString()] = kv.Value;
            return d;
        }

        private sealed class TelemetryOut
        {
            public long seq { get; set; } // temp debug
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
