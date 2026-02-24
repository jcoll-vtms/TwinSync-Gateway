// =====================================================
// COMPLETE REPLACEMENT: IotPlanIngress.cs
// =====================================================
using MQTTnet;
using MQTTnet.Protocol;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    /// <summary>
    /// Ingress for plan/hb/leave messages from AWS IoT.
    ///
    /// Supports BOTH:
    ///  - tenant legacy: twinsync/{tenantId}/{gatewayId}/{verb}/{deviceId}/{userId}
    ///  - multi-device:  twinsync/{tenantId}/{gatewayId}/{verb}/{deviceType}/{deviceId}/{userId}
    ///
    /// You committed to multi-device topics; tenant legacy is kept for compatibility.
    /// </summary>
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
            _mqtt = mqtt ?? throw new ArgumentNullException(nameof(mqtt));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _gatewayId = gatewayId ?? throw new ArgumentNullException(nameof(gatewayId));
            _getTargetByKey = getTargetByKey ?? throw new ArgumentNullException(nameof(getTargetByKey));

            _mqtt.AddMessageHandler(OnMessageAsync);
        }

        public async Task SubscribeAsync(CancellationToken ct)
        {
            var qos = MqttQualityOfServiceLevel.AtLeastOnce;

            // ✅ Only multi-device topics:
            // twinsync/{tenantId}/{gatewayId}/{verb}/{deviceType}/{deviceId}/{userId}
            await TrySubAsync($"twinsync/{_tenantId}/{_gatewayId}/plan/+/+/+", qos, ct).ConfigureAwait(false);
            await TrySubAsync($"twinsync/{_tenantId}/{_gatewayId}/hb/+/+/+", qos, ct).ConfigureAwait(false);
            await TrySubAsync($"twinsync/{_tenantId}/{_gatewayId}/leave/+/+/+", qos, ct).ConfigureAwait(false);

            Log?.Invoke("Ingress subscriptions active (multi-device only).");
        }

        private async Task TrySubAsync(string filter, MqttQualityOfServiceLevel qos, CancellationToken ct)
        {
            try
            {
                await _mqtt.SubscribeAsync(filter, qos, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Don't fail ingress startup if a single filter errors.
                Log?.Invoke($"Subscribe failed '{filter}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private Task OnMessageAsync(MqttApplicationMessage msg)
        {
            try
            {
                if (msg == null) return Task.CompletedTask;

                if (!TryParseTopic(msg.Topic, out var verb, out var key, out var userId))
                    return Task.CompletedTask;

                HandleParsedMessage(verb, key, userId, msg.PayloadSegment);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"IoT ingress handler error: {ex.GetType().Name}: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private bool TryParseTopic(string topic, out string verb, out DeviceKey key, out string userId)
        {
            verb = string.Empty;
            userId = string.Empty;
            key = default;

            // Accepted only:
            // twinsync/{tenantId}/{gatewayId}/{verb}/{deviceType}/{deviceId}/{userId}

            var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 7) return false;
            if (!parts[0].Equals("twinsync", StringComparison.OrdinalIgnoreCase)) return false;

            if (!parts[1].Equals(_tenantId, StringComparison.Ordinal)) return false;
            if (!parts[2].Equals(_gatewayId, StringComparison.Ordinal)) return false;

            verb = parts[3];
            var deviceType = parts[4];
            var deviceId = parts[5];
            userId = parts[6];

            key = new DeviceKey(_tenantId, _gatewayId, deviceId, deviceType);
            return true;
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
                    GO: env.go ?? Array.Empty<int>(),
                    DO: env.@do ?? Array.Empty<int>(),
                    R: env.r ?? Array.Empty<int>(),
                    VAR: env.@var ?? Array.Empty<string>());

                // Your IPlanTarget already contains ApplyTelemetryPlan (robot only).
                // PLC targets can no-op or ignore, but ideally they won't receive telemetry topics.
                target.ApplyTelemetryPlan(userId, plan, env.periodMs);

                Log?.Invoke(
                    $"Telemetry plan user='{userId}' key='{key}' DI={Count(env.di)} GI={Count(env.gi)} GO={Count(env.go)} DO={Count(env.@do)} R={Count(env.r)} VAR={Count(env.@var)} periodMs={env.periodMs?.ToString() ?? "null"}");
                return;
            }

            if (kind.Equals("machineData", StringComparison.OrdinalIgnoreCase))
            {
                if (target is not IMachineDataPlanTarget plcTarget)
                {
                    Log?.Invoke($"MachineData plan ignored (target not PLC) user='{userId}' key='{key}'");
                    return;
                }

                var items = (env.items ?? Array.Empty<PlanItem>())
                    .Where(i => !string.IsNullOrWhiteSpace(i.path))
                    .Select(i => new MachineDataPlanItem(i.path!.Trim(), i.expand))
                    .ToArray();

                var plan = new MachineDataPlan(items);
                plcTarget.ApplyMachineDataPlan(userId, plan, env.periodMs);

                Log?.Invoke($"MachineData plan user='{userId}' key='{key}' items={items.Length} periodMs={env.periodMs?.ToString() ?? "null"}");
                return;
            }

            Log?.Invoke($"Unknown plan kind='{env.kind}' user='{userId}' key='{key}'");
        }

        private static int Count(int[]? x) => x?.Length ?? 0;
        private static int Count(string[]? x) => x?.Length ?? 0;

        private sealed class PlanEnvelope
        {
            public string? kind { get; set; }      // "telemetry" | "machineData"
            public int[]? di { get; set; }
            public int[]? gi { get; set; }
            public int[]? go { get; set; }
                        public int[]? @do { get; set; } // Digital Outputs (DO)
            public int[]? r { get; set; }   // Numeric Registers (R)
            public string[]? @var { get; set; } // Variables (VAR)
            public int? periodMs { get; set; }

            // machineData:
            public PlanItem[]? items { get; set; }
        }

        private sealed class PlanItem
        {
            public string? path { get; set; }
            public string? expand { get; set; } // null | "udt"
        }
    }

    /// <summary>
    /// PLC plan target (new). Keep this separate from IPlanTarget to avoid duplication.
    /// PLC sessions implement this in addition to IPlanTarget.
    /// </summary>
    public interface IMachineDataPlanTarget : IPlanTarget
    {
        void ApplyMachineDataPlan(string userId, MachineDataPlan plan, int? periodMs);
    }
}
