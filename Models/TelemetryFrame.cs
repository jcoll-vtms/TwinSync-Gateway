using System;
using System.Collections.Generic;

namespace TwinSync_Gateway.Models
{
    public sealed record TelemetryFrame(
        DateTimeOffset Timestamp,
        double[]? JointsDeg,
        IReadOnlyDictionary<int, int>? DI,
        IReadOnlyDictionary<int, int>? GI,
        IReadOnlyDictionary<int, int>? GO
    );
}
