using System;
using System.Threading;
using System.Threading.Tasks;

namespace TwinSync_Gateway.Services
{
    public abstract class DeviceSessionBase<TFrame> : IDeviceSession<TFrame>
        where TFrame : IDeviceFrame
    {
        private CancellationTokenSource? _runCts;
        private Task? _runTask;

        public DeviceKey Key { get; }

        public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;

        public event Action<DeviceStatus, Exception?>? StatusChanged;
        public event Action<TFrame>? FrameReceived;
        public event Action<bool>? PublishAllowedChanged;

        private bool _publishAllowed;
        protected bool IsPublishAllowed => _publishAllowed;

        /// <summary>
        /// Default behavior: only read frames when publish is allowed.
        /// Override to false for sessions that should continue producing frames for UI even when not publishing.
        /// </summary>
        protected virtual bool ReadOnlyWhenPublishAllowed => true;

        protected DeviceSessionBase(DeviceKey key)
        {
            Key = key;
        }

        protected abstract Task OnConnectAsync(CancellationToken ct);
        protected abstract Task OnDisconnectAsync(CancellationToken ct);
        protected abstract Task<TFrame> ReadFrameAsync(CancellationToken ct);

        protected void EmitFrame(TFrame frame) => FrameReceived?.Invoke(frame);

        public void SetPublishAllowed(bool allowed)
        {
            if (_publishAllowed == allowed) return;
            _publishAllowed = allowed;
            PublishAllowedChanged?.Invoke(allowed);
        }

        public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

        public async Task ConnectAsync(CancellationToken ct)
        {
            if (Status != DeviceStatus.Disconnected) return;

            SetStatus(DeviceStatus.Connecting, null);

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                await OnConnectAsync(_runCts.Token).ConfigureAwait(false);
                SetStatus(DeviceStatus.Connected, null);

                _runTask = Task.Run(() => RunLoopAsync(_runCts.Token));
            }
            catch (Exception ex)
            {
                SetPublishAllowed(false);
                SetStatus(DeviceStatus.Faulted, ex);

                try { _runCts.Cancel(); } catch { }
                try { _runCts.Dispose(); } catch { }
                _runCts = null;

                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            SetPublishAllowed(false);

            var cts = _runCts;
            var task = _runTask;

            _runCts = null;
            _runTask = null;

            try { cts?.Cancel(); } catch { }

            if (task != null)
            {
                try { await task.ConfigureAwait(false); }
                catch { }
            }

            try
            {
                await OnDisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }

            try { cts?.Dispose(); } catch { }

            SetStatus(DeviceStatus.Disconnected, null);
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                SetStatus(DeviceStatus.Streaming, null);

                while (!ct.IsCancellationRequested)
                {
                    if (ReadOnlyWhenPublishAllowed && !_publishAllowed)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue;
                    }

                    var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
                    EmitFrame(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetPublishAllowed(false);
                SetStatus(DeviceStatus.Faulted, ex);
            }
        }

        private void SetStatus(DeviceStatus s, Exception? ex)
        {
            Status = s;
            StatusChanged?.Invoke(s, ex);
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }
}
