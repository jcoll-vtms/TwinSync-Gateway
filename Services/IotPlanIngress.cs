using MQTTnet;
using MQTTnet.Protocol;
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class IotPlanIngress
    {
        private readonly string _tenantId;
        private readonly string _gatewayId;
        private readonly Func<DeviceKey, IPlanTarget?> _getTargetByKey;
        private readonly IotMqttConnection _mqtt;

        public event Action<string>? Log;

        public IotPlanIngress(
            IotMqttConnection mqtt,
            string tenantId,
            string gatewayId,
            Func<DeviceKey, IPlanTarget?> getTargetByKey)
        {
            _mqtt = mqtt;
            _tenantId = tenantId;
            _gatewayId = gatewayId;
            _getTargetByKey = getTargetByKey;

            _mqtt.AddMessageHandler(OnMessageAsync);
        }

        public async Task SubscribeAsync(CancellationToken ct)
        {
            var qos = MqttQualityOfServiceLevel.AtLeastOnce;

            // Legacy topics
            //await _mqtt.SubscribeAsync($"twinsync/{_gatewayId}/plan/+/+", qos, ct);
            //await _mqtt.SubscribeAsync($"twinsync/{_gatewayId}/hb/+/+", qos, ct);
            //await _mqtt.SubscribeAsync($"twinsync/{_gatewayId}/leave/+/+", qos, ct);

            // Tenant-scoped legacy topics
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/plan/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/hb/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/leave/+/+", qos, ct);

            // New multi-device topics
            // twinsync/{tenantId}/{gatewayId}/{verb}/{deviceType}/{deviceId}/{userId}
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/plan/+/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/hb/+/+/+", qos, ct);
            await _mqtt.SubscribeAsync($"twinsync/{_tenantId}/{_gatewayId}/leave/+/+/+", qos, ct);

            Log?.Invoke("Ingress subscriptions active.");
        }

        private Task OnMessageAsync(MqttApplicationMessage msg)
        {
            try
            {
                if (!TryParseTopic(msg.Topic, out var verb, out var key, out var userId))
                    return Task.CompletedTask;

                HandleParsedMessage(verb, key, userId, msg.PayloadSegment);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"IoT ingress handler error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool TryParseTopic(string topic, out string verb, out DeviceKey key, out string userId)
        {
            verb = string.Empty;
            userId = string.Empty;
            key = default;

            // Accepted:
            // 1) twinsync/{gatewayId}/{verb}/{robot}/{user}
            // 2) twinsync/{tenantId}/{gatewayId}/{verb}/{robot}/{user}
            // 3) twinsync/{tenantId}/{gatewayId}/{verb}/{deviceType}/{deviceId}/{user}

            var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return false;
            if (!parts[0].Equals("twinsync", StringComparison.OrdinalIgnoreCase)) return false;

            // (1) legacy
            if (parts.Length == 5 &&
                parts[1].Equals(_gatewayId, StringComparison.Ordinal))
            {
                verb = parts[2];
                var robotName = parts[3];
                userId = parts[4];

                key = new DeviceKey(_tenantId, _gatewayId, robotName, "robot");
                return true;
            }

            // (2) tenant legacy
            if (parts.Length == 6 &&
                parts[1].Equals(_tenantId, StringComparison.Ordinal) &&
                parts[2].Equals(_gatewayId, StringComparison.Ordinal))
            {
                verb = parts[3];
                var robotName = parts[4];
                userId = parts[5];

                key = new DeviceKey(_tenantId, _gatewayId, robotName, "robot");
                return true;
            }

            // (3) new multi-device
            if (parts.Length == 7 &&
                parts[1].Equals(_tenantId, StringComparison.Ordinal) &&
                parts[2].Equals(_gatewayId, StringComparison.Ordinal))
            {
                verb = parts[3];
                var deviceType = parts[4];
                var deviceId = parts[5];
                userId = parts[6];

                key = new DeviceKey(_tenantId, _gatewayId, deviceId, deviceType);
                return true;
            }

            return false;
        }

        private void HandleParsedMessage(string verb, DeviceKey key, string userId, ArraySegment<byte> payloadSegment)
        {
            var target = _getTargetByKey(key);
            if (target == null)
            {
                Log?.Invoke($"Message ignored (no active session) verb='{verb}' key='{key}' user='{userId}'");
                return;
            }

            if (verb.Equals("hb", StringComparison.OrdinalIgnoreCase))
            {
                target.TouchUser(userId);
                return;
            }

            if (verb.Equals("leave", StringComparison.OrdinalIgnoreCase))
            {
                target.RemoveUser(userId);
                return;
            }

            if (!verb.Equals("plan", StringComparison.OrdinalIgnoreCase))
                return;

            var json = Encoding.UTF8.GetString(payloadSegment.ToArray());

            PlanEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<PlanEnvelope>(json);
            }
            catch
            {
                Log?.Invoke($"Bad JSON plan payload user='{userId}' key='{key}': {json}");
                return;
            }

            if (env == null) return;

            // Default kind for backward compatibility (old clients had only telemetry fields)
            var kind = string.IsNullOrWhiteSpace(env.kind) ? "telemetry" : env.kind;

            if (kind.Equals("telemetry", StringComparison.OrdinalIgnoreCase))
            {
                var plan = new TelemetryPlan(
                    DI: env.di ?? Array.Empty<int>(),
                    GI: env.gi ?? Array.Empty<int>(),
                    GO: env.go ?? Array.Empty<int>());

                // Only robots will implement this today. PLC targets can no-op.
                target.ApplyTelemetryPlan(userId, plan, env.periodMs);

                Log?.Invoke(
                    $"Telemetry plan user='{userId}' key='{key}' DI={Count(env.di)} GI={Count(env.gi)} GO={Count(env.go)} periodMs={env.periodMs?.ToString() ?? "null"}");
                return;
            }

            if (kind.Equals("machineData", StringComparison.OrdinalIgnoreCase))
            {
                // Reserved for PLC plan evolution (tags/registers/etc).
                Log?.Invoke($"MachineData plan received user='{userId}' key='{key}' (not implemented yet).");
                return;
            }

            Log?.Invoke($"Unknown plan kind='{env.kind}' user='{userId}' key='{key}'");
        }

        private static int Count(int[]? x) => x?.Length ?? 0;

        private sealed class PlanEnvelope
        {
            public string? kind { get; set; }      // "telemetry" | "machineData" ...
            public int[]? di { get; set; }
            public int[]? gi { get; set; }
            public int[]? go { get; set; }
            public int? periodMs { get; set; }

            // future:
            // public string[]? tags { get; set; }
            // public string[]? registers { get; set; }
        }
    }
}
