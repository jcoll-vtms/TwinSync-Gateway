using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public interface IPlanTarget
    {
        DeviceKey Key { get; }

        void TouchUser(string userId);
        void RemoveUser(string userId);

        // robot-only for now (PLC later)
        void ApplyTelemetryPlan(string userId, TelemetryPlan plan, int? periodMs);
    }
}
