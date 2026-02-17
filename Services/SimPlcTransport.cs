// ===============================
// 5) Services: SimPlcTransport (supports scalars, arrays, UDT expansion via a type map)
// File: Services/SimPlcTransport.cs
// ===============================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    /// <summary>
    /// Simulator transport for PLC:
    /// - Scalar values: deterministic pseudo-data
    /// - Arrays: supports "Tag[0..9]" range syntax
    /// - UDT expansion: if item.Expand == "udt", uses a type-map: TagName -> member list
    ///   and reads Tag.Member for each member (scalar generation).
    /// 
    /// This lets you validate end-to-end plan → union → polling → publish before choosing libplctag.
    /// </summary>
    public sealed class SimPlcTransport : IPlcTransport
    {
        private bool _connected;
        private PlcConfig? _cfg;
        private long _tick;

        // Simple "UDT member map":
        // Tag base name -> members (you can replace with config file later)
        private readonly Dictionary<string, string[]> _udtMembers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Example UDT root tags and their members:
                ["Station1Status"] = new[] { "Run", "Faulted", "FaultCode", "Speed", "Temp0", "Temp1" },
                ["MotorStatus"] = new[] { "Run", "Cmd", "Amps", "RPM" }
            };

        public Task ConnectAsync(PlcConfig config, CancellationToken ct)
        {
            _cfg = config;
            _connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            _connected = false;
            _cfg = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _connected = false;
            _cfg = null;
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, PlcValue>> ReadAsync(
            MachineDataPlanItem[] items,
            PlcConfig cfg,
            CancellationToken ct)
        {
            if (!_connected) throw new InvalidOperationException("SimPlcTransport not connected.");

            Interlocked.Increment(ref _tick);

            var result = new Dictionary<string, PlcValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                var key = string.IsNullOrWhiteSpace(item.Path) ? "<empty>" : item.Path.Trim();

                if (string.Equals(item.Expand, "udt", StringComparison.OrdinalIgnoreCase))
                {
                    // Expand: create a Struct PlcValue with members
                    var structVal = ReadUdtAsStruct(key, cfg);
                    result[key] = structVal;
                    continue;
                }

                // Array range support: Tag[0..9] or Tag[3..5]
                if (TryParseRange(key, out var baseName, out var start, out var end))
                {
                    var maxElems = cfg.MaxArrayElements <= 0 ? 200 : cfg.MaxArrayElements;
                    var count = Math.Min(maxElems, Math.Max(0, end - start + 1));

                    var arr = new PlcValue[count];
                    for (int i = 0; i < count; i++)
                    {
                        // element "path" can be baseName[index]
                        var elemPath = $"{baseName}[{start + i}]";
                        arr[i] = GenerateScalar(elemPath);
                    }

                    result[key] = new PlcValue(PlcValueKind.Array, arr);
                    continue;
                }

                // Default: scalar
                result[key] = GenerateScalar(key);
            }

            return Task.FromResult((IReadOnlyDictionary<string, PlcValue>)result);
        }

        private PlcValue ReadUdtAsStruct(string udtRoot, PlcConfig cfg)
        {
            // If map exists, use it; otherwise, produce a small default struct.
            if (!_udtMembers.TryGetValue(udtRoot, out var members) || members.Length == 0)
                members = new[] { "MemberA", "MemberB", "MemberC" };

            var maxFields = cfg.MaxStructFields <= 0 ? 200 : cfg.MaxStructFields;
            var take = Math.Min(maxFields, members.Length);

            var dict = new Dictionary<string, PlcValue>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < take; i++)
            {
                var memberName = members[i];
                var memberPath = $"{udtRoot}.{memberName}";
                dict[memberName] = GenerateScalar(memberPath);
            }

            return new PlcValue(PlcValueKind.Struct, dict);
        }

        private PlcValue GenerateScalar(string path)
        {
            // Deterministic pseudo-values based on path + tick.
            // Provides stable-ish behavior across reads, good for UI testing.

            var t = Volatile.Read(ref _tick);

            // BOOL-like tags heuristics
            if (path.EndsWith(".Run", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".Cmd", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".Faulted", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Run", StringComparison.OrdinalIgnoreCase))
            {
                var b = ((t + Hash(path)) % 2) == 0;
                return new PlcValue(PlcValueKind.Bool, b);
            }

            // FaultCode / DINT-like
            if (path.EndsWith("FaultCode", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Code", StringComparison.OrdinalIgnoreCase))
            {
                var v = (int)((t + Hash(path)) % 100);
                return new PlcValue(PlcValueKind.Int32, v);
            }

            // Level / Speed / RPM / Amps - numeric
            if (path.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("RPM", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Amps", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Temp", StringComparison.OrdinalIgnoreCase))
            {
                // Smooth-ish numeric
                var x = (t % 1000) / 1000.0;
                var v = Math.Round(10.0 + 90.0 * x + (Hash(path) % 7), 2);
                return new PlcValue(PlcValueKind.Double, v);
            }

            // Default: string-ish stable
            return new PlcValue(PlcValueKind.String, $"sim:{path}:{t}");
        }

        private static bool TryParseRange(string s, out string baseName, out int start, out int end)
        {
            // Supports: Tag[0..9] (no spaces)
            baseName = string.Empty;
            start = 0;
            end = -1;

            var m = Regex.Match(s, @"^(?<base>[^\[]+)\[(?<a>\d+)\.\.(?<b>\d+)\]$");
            if (!m.Success) return false;

            baseName = m.Groups["base"].Value.Trim();
            if (!int.TryParse(m.Groups["a"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) return false;
            if (!int.TryParse(m.Groups["b"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out end)) return false;
            if (end < start) (start, end) = (end, start);
            return !string.IsNullOrWhiteSpace(baseName);
        }

        private static int Hash(string s)
        {
            unchecked
            {
                int h = 23;
                for (int i = 0; i < s.Length; i++)
                    h = h * 31 + s[i];
                return h;
            }
        }
    }
}
