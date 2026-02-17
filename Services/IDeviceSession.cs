// ===============================
// 7) Services: IDeviceSession<T> (if you don't already have it exactly)
// File: Services/IDeviceSession.cs
// ===============================
using System;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public interface IDeviceSession<TFrame> : IAsyncDisposable where TFrame : IDeviceFrame
    {
        DeviceKey Key { get; }
        DeviceStatus Status { get; }

        event Action<DeviceStatus, Exception?>? StatusChanged;
        event Action<TFrame>? FrameReceived;
        event Action<bool>? PublishAllowedChanged;

        Task ConnectAsync();
        Task ConnectAsync(CancellationToken ct);
        Task DisconnectAsync();

        void SetPublishAllowed(bool allowed);
    }
}
