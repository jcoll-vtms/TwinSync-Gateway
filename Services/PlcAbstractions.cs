// ===============================
// 4) Services: PLC plan target + PLC transport interfaces
// File: Services/PlcAbstractions.cs
// ===============================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public interface IPlcTransport : IAsyncDisposable
    {
        Task ConnectAsync(PlcConfig config, CancellationToken ct);
        Task DisconnectAsync(CancellationToken ct);

        /// <summary>
        /// Reads the requested items and returns a mapping keyed by item "key" (typically item.Path).
        /// Expand handling (UDT) is transport/session responsibility.
        /// </summary>
        Task<IReadOnlyDictionary<string, PlcValue>> ReadAsync(
            MachineDataPlanItem[] items,
            PlcConfig cfg,
            CancellationToken ct);
    }
}
