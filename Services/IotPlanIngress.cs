using MQTTnet;
using MQTTnet.Protocol;
using System.Text;
using System.Text.Json;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class IotPlanIngress
    {
        private readonly string _tenantId;
        private readonly string _gatewayId;
        private readonly Func<string, RobotSession?> _getSessionByRobot;
        private readonly IotMqttConnection _mqtt;

        public event Action<string>? Log;

        public IotPlanIngress(
            IotMqttConnection mqtt,
            string tenantId,
            string gatewayId,
            Func<string, RobotSession?> getSessionByRobot)
        {
            _mqtt = mqtt;
            _tenantId = tenantId;
            _gatewayId = gatewayId;
            _getSessionByRobot = getSessionByRobot;

            // Register message handler once
            _mqtt.AddMessageHandler(OnMessageAsync);
        }

        public async Task SubscribeAsync(CancellationToken ct)
        {
            var qos = MqttQualityOfServiceLevel.AtLeastOnce;

            await _mqtt.SubscribeAsync($"twinsync/{_gatewayId}/plan/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_gatewayId}/hb/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_gatewayId}/leave/+/+", qos, ct);

            // Tenant topics (new)
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/plan/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/hb/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/leave/+/+", qos, ct);

            Log?.Invoke("Ingress subscriptions active.");
        }

        private Task OnMessageAsync(MqttApplicationMessage msg)
        {
            try
            {
                HandleMessage(msg.Topic, msg.PayloadSegment);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"IoT ingress handler error: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        private void HandleMessage(string topic, ArraySegment<byte> payloadSegment)
        {
            // topic: twinsync/{gatewayId}/{verb}/{robotName}/{userId}
            var parts = topic.Split('/');
            if (parts.Length < 5) return;

            // Legacy: twinsync/{gatewayId}/{verb}/{robot}/{user}
            // Tenant: twinsync/{tenantId}/{gatewayId}/{verb}/{robot}/{user}
            string verb, robotName, userId;

            if (parts.Length >= 6 &&
            string.Equals(parts[0], "twinsync", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[1], _tenantId, StringComparison.Ordinal) &&
            string.Equals(parts[2], _gatewayId, StringComparison.Ordinal))
            {
                verb = parts[3];
                robotName = parts[4];
                userId = parts[5];
            }
            else if (
            string.Equals(parts[0], "twinsync", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[1], _gatewayId, StringComparison.Ordinal))
            {
                verb = parts[2];
                robotName = parts[3];
                userId = parts[4];
            }
            else
            {
                return;
            }

            var session = _getSessionByRobot(robotName);
            if (session == null)
            {
                Log?.Invoke($"Message ignored (no active session) verb='{verb}' robot='{robotName}' user='{userId}'");
                return;
            }

            if (string.Equals(verb, "hb", StringComparison.OrdinalIgnoreCase))
            {
                session.TouchUser(userId);
                return;
            }

            if (string.Equals(verb, "leave", StringComparison.OrdinalIgnoreCase))
            {
                session.RemoveUser(userId);
                return;
            }

            if (!string.Equals(verb, "plan", StringComparison.OrdinalIgnoreCase))
                return;

            var json = Encoding.UTF8.GetString(
                payloadSegment.Array!,
                payloadSegment.Offset,
                payloadSegment.Count);

            PlanMessage? msgObj;
            try
            {
                msgObj = JsonSerializer.Deserialize<PlanMessage>(json);
            }
            catch
            {
                Log?.Invoke($"Bad JSON plan payload from '{userId}' for '{robotName}': {json}");
                return;
            }

            if (msgObj == null) return;

            var plan = new TelemetryPlan(
                DI: msgObj.di ?? Array.Empty<int>(),
                GI: msgObj.gi ?? Array.Empty<int>(),
                GO: msgObj.go ?? Array.Empty<int>());

            session.SetUserPlan(userId, plan);

            Log?.Invoke($"Plan applied user='{userId}' robot='{robotName}' DI={Count(msgObj.di)} GI={Count(msgObj.gi)} GO={Count(msgObj.go)}");
        }

        private static int Count(int[]? x) => x?.Length ?? 0;

        private sealed class PlanMessage
        {
            public int[]? di { get; set; }
            public int[]? gi { get; set; }
            public int[]? go { get; set; }
            public int? periodMs { get; set; }
        }
    }
}
