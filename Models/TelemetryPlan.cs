using System.Collections.Generic;

namespace TwinSync_Gateway.Models
{
    public sealed record TelemetryPlan(
        IReadOnlyCollection<int> DI,
        IReadOnlyCollection<int> GI,
        IReadOnlyCollection<int> GO)
    {
        public static readonly TelemetryPlan Empty =
            new TelemetryPlan(System.Array.Empty<int>(), System.Array.Empty<int>(), System.Array.Empty<int>());
    }
}
