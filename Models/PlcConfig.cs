using System;

namespace TwinSync_Gateway.Models
{
    public sealed class PlcConfig
    {
        public string Name { get; set; } = "PLC1";

        // ControlLogix / CompactLogix
        public string IpAddress { get; set; } = "192.168.1.10";
        public int Port { get; set; } = 44818;

        // Chassis CPU slot (common default is 0)
        public int Slot { get; set; } = 0;

        // libplctag connection hints (Phase 2)
        // Example: "controllogix" or "compactlogix"
        public string PlcType { get; set; } = "controllogix";

        // libplctag "path" route (very commonly required).
        // Default is derived from Slot as "1,{Slot}" e.g. "1,0"
        public string? Path { get; set; }

        // Polling defaults / guardrails
        public int DefaultPeriodMs { get; set; } = 200;
        public int TimeoutMs { get; set; } = 1000;
        public int MaxItems { get; set; } = 50;
        public int MaxArrayElements { get; set; } = 200;
        public int MaxStructFields { get; set; } = 200;

        // Optional stable ID
        public Guid Id { get; set; } = Guid.NewGuid();

        public string EffectivePath
            => string.IsNullOrWhiteSpace(Path) ? $"1,{Slot}" : Path!.Trim();
    }
}
