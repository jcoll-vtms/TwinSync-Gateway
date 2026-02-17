// ===============================
// 1) Models: PlcValue, PlcFrame, MachineDataPlan
// File: Models/PlcModels.cs
// ===============================
using System;
using System.Collections.Generic;
using TwinSync_Gateway.Services;

namespace TwinSync_Gateway.Models
{
    public enum PlcValueKind
    {
        Null,
        Bool,
        Int32,
        Int64,
        Float,
        Double,
        String,
        Bytes,
        Array,
        Struct
    }

    /// <summary>
    /// JSON-friendly typed value container for PLC data.
    /// - Scalar kinds: Value is bool/int/double/string/byte[]
    /// - Array: Value is PlcValue[]
    /// - Struct: Value is Dictionary&lt;string, PlcValue&gt;
    /// </summary>
    public sealed record PlcValue(PlcValueKind Kind, object? Value);

    public sealed record PlcFrame(
        DateTimeOffset Timestamp,
        long Sequence,
        IReadOnlyDictionary<string, PlcValue> Values
    ) : IDeviceFrame;

    public sealed record MachineDataPlanItem(string Path, string? Expand = null); // Expand: null | "udt"
    public sealed record MachineDataPlan(MachineDataPlanItem[] Items);
}
