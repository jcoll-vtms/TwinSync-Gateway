using System;
using System.Threading.Tasks;

namespace TwinSync_Gateway.Services
{
    public interface IDeviceSession<out TFrame> : IAsyncDisposable
        where TFrame : IDeviceFrame
    {
        DeviceKey Key { get; }
        DeviceStatus Status { get; }

        event Action<DeviceStatus, Exception?>? StatusChanged;
        event Action<TFrame>? FrameReceived;

        /// <summary>
        /// Indicates whether cloud publishing is allowed (gated by user presence/leases/etc).
        /// Derived sessions can use this to reduce upstream load.
        /// </summary>
        event Action<bool>? PublishAllowedChanged;

        Task ConnectAsync();
        Task DisconnectAsync();

        void SetPublishAllowed(bool allowed);
    }
}
