using System;
using System.Collections.Generic;

namespace TwinSync_Gateway.Models
{
    public sealed record TelemetryPlan(
        IReadOnlyCollection<int> DI,
        IReadOnlyCollection<int> GI,
        IReadOnlyCollection<int> GO,
        IReadOnlyCollection<int> DO,
        IReadOnlyCollection<int> R,
        IReadOnlyCollection<string> VAR)
    {
        public IReadOnlyCollection<int> DI { get; init; } = DI ?? Array.Empty<int>();
        public IReadOnlyCollection<int> GI { get; init; } = GI ?? Array.Empty<int>();
        public IReadOnlyCollection<int> GO { get; init; } = GO ?? Array.Empty<int>();

        public IReadOnlyCollection<int> DO { get; init; } = DO ?? Array.Empty<int>();
        public IReadOnlyCollection<int> R { get; init; } = R ?? Array.Empty<int>();
        public IReadOnlyCollection<string> VAR { get; init; } = VAR ?? Array.Empty<string>();

        public static readonly TelemetryPlan Empty =
            new TelemetryPlan(
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<string>());
    }
}
