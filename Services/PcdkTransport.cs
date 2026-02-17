using System;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class PcdkTransport : IRobotTransport
    {
        public string Name => "PCDK";

        // Stub state so the rest of the system can reason about connectivity.
        // When you implement real PCDK, wire this to actual connection state.
        private bool _isConnected;

        public bool IsConnected => _isConnected;

        public Task ConnectAsync(RobotConfig config, CancellationToken ct)
        {
            _isConnected = false;

            // Next: reference FANUC PCDK assemblies and connect via Robot Server.
            // For now, throw a clear message so UI wiring is testable.
            throw new NotImplementedException(
                "PCDK transport not wired yet. Add FANUC PCDK references and implement ConnectAsync.");
        }

        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _isConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
