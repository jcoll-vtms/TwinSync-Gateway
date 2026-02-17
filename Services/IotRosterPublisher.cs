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
            _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _gatewayId = gatewayId ?? throw new ArgumentNullException(nameof(gatewayId));
        }

        public sealed record DeviceRosterEntry(
            string deviceId,
            string deviceType,
            string displayName,
            string status,
            string connectionType,
            long? lastDataMs);

        /// <summary>
        /// Publish retained device roster to:
        ///   twinsync/{tenantId}/{gatewayId}/devices   (retain=true)
        ///
        /// Payload:
        /// {
        ///   ts, tenantId, gatewayId,
        ///   devices: [{ deviceId, deviceType, displayName, status, connectionType, lastDataMs }]
        /// }
        /// </summary>
        public async Task PublishDevicesAsync(
            IEnumerable<DeviceRosterEntry> devices,
            CancellationToken ct)
        {
            if (!_mqtt.IsConnected) return;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var topic = $"twinsync/{_tenantId}/{_gatewayId}/devices";

            var payload = new
            {
                ts = nowMs,
                tenantId = _tenantId,
                gatewayId = _gatewayId,
                devices = (devices ?? Enumerable.Empty<DeviceRosterEntry>())
                    .Select(d => new
                    {
                        deviceId = d.deviceId,
                        deviceType = d.deviceType,
                        displayName = d.displayName,
                        status = d.status,
                        connectionType = d.connectionType,
                        lastDataMs = d.lastDataMs
                    })
                    .ToArray()
            };

            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                await _mqtt.PublishAsync(
                    topic,
                    bytes,
                    qos: MqttQualityOfServiceLevel.AtLeastOnce,
                    retain: true,
                    ct).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine($"[Roster] Published /devices bytes={bytes.Length}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Roster] Publish FAILED: {ex}");
            }
        }
    }
}
