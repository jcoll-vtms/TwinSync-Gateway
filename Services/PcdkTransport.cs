using System;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class PcdkTransport : IRobotTransport
    {
        public string Name => "PCDK";

        public Task ConnectAsync(RobotConfig config, CancellationToken ct)
        {
            // Next: reference FANUC PCDK assemblies and connect via Robot Server.
            // For now, just throw a clear message so UI wiring is testable.
            throw new NotImplementedException("PCDK transport not wired yet. Add FANUC PCDK references and implement ConnectAsync.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
