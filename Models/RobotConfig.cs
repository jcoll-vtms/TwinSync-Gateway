using System;
using System.Text.Json.Serialization;

namespace TwinSync_Gateway.Models
{
    public enum ConnectionType
    {
        KarelSocket = 0,
        Pcdk = 1
    }

    public sealed class RobotConfig
    {
        public string Name { get; set; } = "Robot1";

        public ConnectionType ConnectionType { get; set; } = ConnectionType.KarelSocket;

        // Common
        public string IpAddress { get; set; } = "127.0.0.1";

        // KAREL socket
        public int KarelPort { get; set; } = 59002;

        // PCDK (examples — adjust to your actual needs)
        public string? PcdkRobotName { get; set; } = null; // if you need it
        public int PcdkTimeoutMs { get; set; } = 3000;

        // Optional: unique ID so rename doesn't break references
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
