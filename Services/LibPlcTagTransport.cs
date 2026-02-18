using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;
using libplctag;

namespace TwinSync_Gateway.Services
{
    public sealed class LibPlcTagTransport : IPlcTransport
    {
        private bool _connected;

        // Cache: (tagName|kindHint) -> Tag
        private readonly Dictionary<string, Tag> _tags = new(StringComparer.OrdinalIgnoreCase);

        // Same idea as simulator for UDT expansion (you can later move to config)
        private readonly Dictionary<string, string[]> _udtMembers =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Station1Status"] = new[] { "Run", "Faulted", "FaultCode", "Speed", "Temp0", "Temp1" },
                ["MotorStatus"] = new[] { "Run", "Cmd", "Amps", "RPM" }
            };

        public Task ConnectAsync(PlcConfig config, CancellationToken ct)
        {
            _connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            _connected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var kv in _tags)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _tags.Clear();

            _connected = false;
            return ValueTask.CompletedTask;
        }

        public async Task<IReadOnlyDictionary<string, PlcValue>> ReadAsync(
            MachineDataPlanItem[] items,
            PlcConfig cfg,
            CancellationToken ct)
        {
            if (!_connected) throw new InvalidOperationException("LibPlcTagTransport not connected.");

            var result = new Dictionary<string, PlcValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                var key = string.IsNullOrWhiteSpace(item.Path) ? "<empty>" : item.Path.Trim();

                if (string.Equals(item.Expand, "udt", StringComparison.OrdinalIgnoreCase))
                {
                    result[key] = await ReadUdtAsStructAsync(key, cfg, ct).ConfigureAwait(false);
                    continue;
                }

                // Array range: Tag[0..9]
                if (TryParseRange(key, out var baseName, out var start, out var end))
                {
                    var maxElems = cfg.MaxArrayElements <= 0 ? 200 : cfg.MaxArrayElements;
                    var count = Math.Min(maxElems, Math.Max(0, end - start + 1));

                    var arr = new PlcValue[count];
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var elemPath = $"{baseName}[{start + i}]";
                        arr[i] = await ReadScalarAsync(elemPath, cfg, ct).ConfigureAwait(false);
                    }

                    result[key] = new PlcValue(PlcValueKind.Array, arr);
                    continue;
                }

                result[key] = await ReadScalarAsync(key, cfg, ct).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<PlcValue> ReadUdtAsStructAsync(string udtRoot, PlcConfig cfg, CancellationToken ct)
        {
            if (!_udtMembers.TryGetValue(udtRoot, out var members) || members.Length == 0)
                members = new[] { "MemberA", "MemberB", "MemberC" };

            var maxFields = cfg.MaxStructFields <= 0 ? 200 : cfg.MaxStructFields;
            var take = Math.Min(maxFields, members.Length);

            var dict = new Dictionary<string, PlcValue>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < take; i++)
            {
                ct.ThrowIfCancellationRequested();

                var memberName = members[i];
                var memberPath = $"{udtRoot}.{memberName}";
                dict[memberName] = await ReadScalarAsync(memberPath, cfg, ct).ConfigureAwait(false);
            }

            return new PlcValue(PlcValueKind.Struct, dict);
        }

        private async Task<PlcValue> ReadScalarAsync(string tagName, PlcConfig cfg, CancellationToken ct)
        {
            var hint = GuessKind(tagName);
            var tag = GetOrCreateTag(tagName, hint, cfg);

            ct.ThrowIfCancellationRequested();

            // Many libplctag.NET versions expose ReadAsync() without CancellationToken.
            // We rely on Tag.Timeout for bounded reads and use ct between tags.
            await tag.ReadAsync().ConfigureAwait(false);

            try
            {
                return hint switch
                {
                    KindHint.Bool => new PlcValue(PlcValueKind.Bool, tag.GetBit(0)),
                    KindHint.Float => new PlcValue(PlcValueKind.Float, tag.GetFloat32(0)),
                    KindHint.Double => new PlcValue(PlcValueKind.Double, tag.GetFloat64(0)),
                    KindHint.Int64 => new PlcValue(PlcValueKind.Int64, tag.GetInt64(0)),
                    _ => new PlcValue(PlcValueKind.Int32, tag.GetInt32(0)),
                };
            }
            catch
            {
                // Fallback attempts (kept minimal for Phase 2)
                try { return new PlcValue(PlcValueKind.Int32, tag.GetInt32(0)); } catch { }
                try { return new PlcValue(PlcValueKind.Bool, tag.GetBit(0)); } catch { }
                try { return new PlcValue(PlcValueKind.Float, tag.GetFloat32(0)); } catch { }
                try { return new PlcValue(PlcValueKind.Double, tag.GetFloat64(0)); } catch { }
                try { return new PlcValue(PlcValueKind.Int64, tag.GetInt64(0)); } catch { }

                return new PlcValue(PlcValueKind.String, "<unreadable>");
            }
        }

        private Tag GetOrCreateTag(string tagName, KindHint hint, PlcConfig cfg)
        {
            var cacheKey = $"{tagName}|{hint}";
            if (_tags.TryGetValue(cacheKey, out var existing))
                return existing;

            var plcType = ParsePlcType(cfg.PlcType);
            var path = cfg.EffectivePath; // "1,{Slot}" unless overridden
            var timeoutMs = cfg.TimeoutMs <= 0 ? 8000 : cfg.TimeoutMs;

            var tag = new Tag
            {
                Name = tagName,
                Gateway = cfg.IpAddress,
                Path = path,

                // ✅ enums, not strings
                Protocol = Protocol.ab_eip,
                PlcType = plcType,

                // ✅ TimeSpan, not int
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            };

            // Allocate and validate handle up-front (throws on bad config/tag)
            tag.Initialize();

            _tags[cacheKey] = tag;
            return tag;
        }

        private static PlcType ParsePlcType(string? plcType)
        {
            var s = (plcType ?? "").Trim();

            if (s.Equals("controllogix", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("controlLogix", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("control_logix", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("lgx", StringComparison.OrdinalIgnoreCase))
                return PlcType.ControlLogix;

            if (s.Equals("micrologix", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("microLogix", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("micro_logix", StringComparison.OrdinalIgnoreCase))
                return PlcType.MicroLogix;

            // Safe default
            return PlcType.ControlLogix;
        }

        private enum KindHint { Int32, Int64, Bool, Float, Double }

        private static KindHint GuessKind(string tagName)
        {
            var s = tagName;

            if (s.EndsWith(".Run", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".Cmd", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".Faulted", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Bool", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Enable", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Running", StringComparison.OrdinalIgnoreCase))
                return KindHint.Bool;

            if (s.Contains("LREAL", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Double", StringComparison.OrdinalIgnoreCase))
                return KindHint.Double;

            if (s.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Float", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Temp", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("RPM", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Amps", StringComparison.OrdinalIgnoreCase))
                return KindHint.Float;

            if (s.Contains("Int64", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("LINT", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("UDINT", StringComparison.OrdinalIgnoreCase))
                return KindHint.Int64;

            return KindHint.Int32;
        }

        private static bool TryParseRange(string s, out string baseName, out int start, out int end)
        {
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
    }
}
