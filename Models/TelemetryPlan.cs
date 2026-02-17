using System;
using System.Collections.Generic;

namespace TwinSync_Gateway.Models
{
    public sealed record TelemetryPlan(
        IReadOnlyCollection<int> DI,
        IReadOnlyCollection<int> GI,
        IReadOnlyCollection<int> GO)
    {
        public IReadOnlyCollection<int> DI { get; init; } = DI ?? Array.Empty<int>();
        public IReadOnlyCollection<int> GI { get; init; } = GI ?? Array.Empty<int>();
        public IReadOnlyCollection<int> GO { get; init; } = GO ?? Array.Empty<int>();

        public static readonly TelemetryPlan Empty =
            new TelemetryPlan(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
    }
}
