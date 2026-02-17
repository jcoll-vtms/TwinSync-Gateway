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

        // Polling defaults / guardrails
        public int DefaultPeriodMs { get; set; } = 200;
        public int TimeoutMs { get; set; } = 1000;
        public int MaxItems { get; set; } = 50;
        public int MaxArrayElements { get; set; } = 200;
        public int MaxStructFields { get; set; } = 200;

        // Optional stable ID
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
