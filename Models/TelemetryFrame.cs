using System;
using System.Collections.Generic;
using TwinSync_Gateway.Services;

namespace TwinSync_Gateway.Models
{
    public sealed record TelemetryFrame(
        DateTimeOffset Timestamp,
        long Sequence,
        double[]? JointsDeg,
        IReadOnlyDictionary<int, int>? DI,
        IReadOnlyDictionary<int, int>? GI,
        IReadOnlyDictionary<int, int>? GO,
        IReadOnlyDictionary<int, int>? DO,
        IReadOnlyDictionary<int, TelemetryRegisterValue>? R,
        IReadOnlyDictionary<string, string>? VAR
    ) : IDeviceFrame;

    public sealed record TelemetryRegisterValue(int IntVal, double RealVal);
}
