using MQTTnet.Protocol;
using System.Text.Json;
using System.Windows.Interop;

namespace TwinSync_Gateway.Services
{
    public sealed class IotRosterPublisher
    {
        private readonly IotMqttConnection _mqtt;
        private readonly string _tenantId;
        private readonly string _gatewayId;

        public IotRosterPublisher(IotMqttConnection mqtt, string tenantId, string gatewayId)
        {
            _mqtt = mqtt;
            _tenantId = tenantId;
            _gatewayId = gatewayId;
        }

        public async Task PublishAsync(IEnumerable<(string Name, string Status, string ConnectionType, long? LastTelemetryMs)> robots, CancellationToken ct)
        {
            if (!_mqtt.IsConnected) return;

            var topic = $"twinsync/{_tenantId}/{_gatewayId}/robots";
            System.Diagnostics.Debug.WriteLine($"[Roster] {topic}");

            var payload = new
            {
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                tenantId = _tenantId,
                gatewayId = _gatewayId,
                robots = robots.Select(r => new { name = r.Name, status = r.Status, connectionType = r.ConnectionType, lastTelemetryMs = r.LastTelemetryMs }).ToArray()
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);

            try
            {
                await _mqtt.PublishAsync(topic, bytes, MqttQualityOfServiceLevel.AtLeastOnce, retain: true, ct).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[Roster] Published bytes={bytes.Length}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Roster] Publish FAILED: {ex}");
                // swallow — roster is best-effort
            }
        }

    }
}
