using System;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public interface IRobotTransport : IAsyncDisposable
    {
        string Name { get; }

        /// <summary>Connect to the robot using the provided config.</summary>
        Task ConnectAsync(RobotConfig config, CancellationToken ct);

        /// <summary>
        /// Gracefully disconnect (idempotent). Must not throw if already disconnected.
        /// </summary>
        Task DisconnectAsync(CancellationToken ct);

        /// <summary>True if the underlying connection is established.</summary>
        bool IsConnected { get; }
    }
}
