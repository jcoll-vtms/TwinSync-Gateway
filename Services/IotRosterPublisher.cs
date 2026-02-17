using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// Backward compatible robot roster publish.
        /// ALSO publishes the new device roster format at /devices.
        /// </summary>
        public async Task PublishAsync(
            IEnumerable<(string Name, string Status, string ConnectionType, long? LastTelemetryMs)> robots,
            CancellationToken ct)
        {
            if (!_mqtt.IsConnected) return;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Legacy topic/payload (robot-specific)
            var legacyTopic = $"twinsync/{_tenantId}/{_gatewayId}/robots";

            var legacyPayload = new
            {
                ts = nowMs,
                tenantId = _tenantId,
                gatewayId = _gatewayId,
                robots = robots.Select(r => new
                {
                    name = r.Name,
                    status = r.Status,
                    connectionType = r.ConnectionType,
                    lastTelemetryMs = r.LastTelemetryMs
                }).ToArray()
            };

            // New topic/payload (multi-device)
            var devicesTopic = $"twinsync/{_tenantId}/{_gatewayId}/devices";

            var devicesPayload = new
            {
                ts = nowMs,
                tenantId = _tenantId,
                gatewayId = _gatewayId,
                devices = robots.Select(r => new
                {
                    deviceId = r.Name,
                    deviceType = "robot",
                    displayName = r.Name,
                    status = r.Status,
                    connectionType = r.ConnectionType,
                    lastDataMs = r.LastTelemetryMs
                }).ToArray()
            };

            try
            {
                var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacyPayload);
                await _mqtt.PublishAsync(
                    legacyTopic,
                    legacyBytes,
                    qos: MqttQualityOfServiceLevel.AtLeastOnce,
                    retain: true,
                    ct).ConfigureAwait(false);

                var devicesBytes = JsonSerializer.SerializeToUtf8Bytes(devicesPayload);
                await _mqtt.PublishAsync(
                    devicesTopic,
                    devicesBytes,
                    qos: MqttQualityOfServiceLevel.AtLeastOnce,
                    retain: true,
                    ct).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine($"[Roster] Published legacy bytes={legacyBytes.Length} and devices bytes={devicesBytes.Length}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Roster] Publish FAILED: {ex}");
                // roster is best-effort
            }
        }
    }
}
