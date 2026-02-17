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

            // ✅ Only new topic/payload (multi-device)
            var devicesTopic = $"twinsync/{_tenantId}/{_gatewayId}/devices";

            var devicesPayload = new
            {
                ts = nowMs,
                tenantId = _tenantId,
                gatewayId = _gatewayId,
                devices = robots.Select(r => new
                {
                    deviceId = r.Name,
                    deviceType = "fanuc-karel",   // ✅ match RobotSession.Key.DeviceType
                    displayName = r.Name,
                    status = r.Status,
                    connectionType = r.ConnectionType,
                    lastDataMs = r.LastTelemetryMs
                }).ToArray()
            };

            try
            {
                var devicesBytes = JsonSerializer.SerializeToUtf8Bytes(devicesPayload);
                await _mqtt.PublishAsync(
                    devicesTopic,
                    devicesBytes,
                    qos: MqttQualityOfServiceLevel.AtLeastOnce,
                    retain: true,
                    ct).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine($"[Roster] Published devices bytes={devicesBytes.Length}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Roster] Publish FAILED: {ex}");
            }
        }

    }
}
