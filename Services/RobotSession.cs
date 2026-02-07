// RobotSession.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    /// <summary>
    /// Manages a single robot connection + streaming loop.
    /// - Auto-reconnect ON by default
    /// - Auto-resume streaming after reconnect (if user requested streaming)
    /// - Uses END-framed GET_FAST responses to avoid backlog/delay
    /// </summary>
    public sealed class RobotSession : IAsyncDisposable
    {
        private sealed class UserPlanState
        {
            public TelemetryPlan Plan { get; set; } = default!;
            public DateTime LastSeenUtc { get; set; }
        }

        public event Action<string>? Log; // optional (wire to UI/debug if you want)

        // Lease -based user plan cleanup (optional): if a user hasn't sent a heartbeat within the lease timeout,
        // we can remove their plan to free up robot resources. This is not strictly necessary if users are well-behaved,
        // but can help in cases where clients disconnect without cleanup.
        private readonly TimeSpan _leaseTimeout = TimeSpan.FromSeconds(60);
        private readonly TimeSpan _reapInterval = TimeSpan.FromSeconds(5);
        private CancellationTokenSource? _leaseCts;
        private Task? _leaseTask;

        private readonly SemaphoreSlim _ioLock = new(1, 1);

        // User plans (union model)
        private readonly object _userPlansLock = new();
        private readonly Dictionary<string, UserPlanState> _userPlans = new();

        // Last applied plan (what robot currently has)
        private TelemetryPlan _appliedPlan = TelemetryPlan.Empty;

        // Latest desired union plan (computed from userPlans)
        private TelemetryPlan _desiredPlan = TelemetryPlan.Empty;

        // KAREL caps (must match GW_SERV5)
        private const int MaxDi = 10;
        private const int MaxGi = 10;
        private const int MaxGo = 10;

        private readonly RobotConfig _config;
        private readonly Func<IRobotTransport> _transportFactory;

        private IRobotTransport? _transport;

        private CancellationTokenSource? _cts;
        private Task? _runTask;

        private CancellationTokenSource? _streamCts;
        private Task? _streamTask;

        // Connect completion signal for ConnectAsync()
        private TaskCompletionSource<bool>? _connectedTcs;

        // Connection-loss signal (set when streaming detects socket death)
        private TaskCompletionSource<bool>? _connectionLostTcs;

        // Auto-resume intent (ON by default)
        private volatile bool _desiredStreaming = true;
        private volatile int _streamPeriodMs = 30;

        public event Action<RobotStatus, string?>? StatusChanged;
        public event Action<double[]>? JointsUpdated;
        public event Action<TelemetryFrame>? TelemetryUpdated;

        public RobotSession(RobotConfig config, Func<IRobotTransport> transportFactory)
        {
            _config = config;
            _transportFactory = transportFactory;
        }

        public void SetUserPlan(string userId, TelemetryPlan plan)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;
            if (plan is null) return;

            var now = DateTime.UtcNow;

            lock (_userPlansLock)
            {
                _userPlans[userId] = new UserPlanState
                {
                    Plan = plan,
                    LastSeenUtc = now
                };
            }

            _desiredPlan = ComputeUnionPlan();

            // Fire-and-forget apply (safe: guarded by _ioLock + checks)
            _ = ApplyPlanIfChangedAsync(CancellationToken.None);
        }

        public void TouchUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;

            var now = DateTime.UtcNow;

            lock (_userPlansLock)
            {
                if (_userPlans.TryGetValue(userId, out var state))
                    state.LastSeenUtc = now;
            }
        }

        public void RemoveUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;

            bool removed;
            lock (_userPlansLock)
                removed = _userPlans.Remove(userId);

            if (!removed) return;

            _desiredPlan = ComputeUnionPlan();
            _ = ApplyPlanIfChangedAsync(CancellationToken.None);
        }

        public TelemetryPlan GetDesiredPlan() => _desiredPlan;
        public TelemetryPlan GetAppliedPlan() => _appliedPlan;

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_cts != null) return; // already running

            _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runTask = RunAsync(_cts.Token);

            await _connectedTcs.Task.ConfigureAwait(false);

            // Start lease reaper after successful connection (optional cleanup of stale user plans)
            StartLeaseReaper();
        }

        public async Task DisconnectAsync()
        {
            // User intent: stop streaming and don't auto-resume until StartStreamingAsync called again
            _desiredStreaming = false;

            // Stop lease reaper first to avoid it trying to apply plans while we're shutting down / after we've cleared the transport
            StopLeaseReaper();

            await StopStreamingInternalAsync().ConfigureAwait(false);

            if (_cts == null) return;

            try { _cts.Cancel(); } catch { }

            await DisposeTransportQuietlyAsync().ConfigureAwait(false);

            try
            {
                if (_runTask != null)
                    await _runTask.ConfigureAwait(false);
            }
            catch { /* swallow */ }

            try { _cts.Dispose(); } catch { }
            _cts = null;
            _runTask = null;
            _connectedTcs = null;
            _connectionLostTcs = null;

            StatusChanged?.Invoke(RobotStatus.Disconnected, null);
        }

        public Task StartStreamingAsync(int periodMs = 30)
        {
            _desiredStreaming = true;
            _streamPeriodMs = periodMs;

            // If not connected yet, RunAsync will auto-start streaming afterúú
            if (_transport == null)
                return Task.CompletedTask;

            if (_streamCts != null)
                return Task.CompletedTask;

            _streamCts = new CancellationTokenSource();
            _streamTask = StreamLoopAsync(periodMs, _streamCts.Token);

            StatusChanged?.Invoke(RobotStatus.Streaming, null);
            return Task.CompletedTask;
        }

        public async Task StopStreamingAsync()
        {
            _desiredStreaming = false;
            await StopStreamingInternalAsync().ConfigureAwait(false);
        }

        // Optional lease reaper for cleaning up stale user plans. Not strictly necessary,
        // but can help in cases where clients disconnect without cleanup.
        private void StartLeaseReaper()
        {
            if (_leaseTask != null) return;

            _leaseCts = new CancellationTokenSource();
            var ct = _leaseCts.Token;

            _leaseTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_reapInterval, ct).ConfigureAwait(false);
                        ReapExpiredUsers();
                    }
                    catch (OperationCanceledException) { }
                    catch { /* swallow */ }
                }
            }, ct);
        }

        // Reap users whose last heartbeat is too old, then update the union plan if needed
        private void StopLeaseReaper()
        {
            var cts = _leaseCts;
            var task = _leaseTask;

            _leaseCts = null;
            _leaseTask = null;

            try { cts?.Cancel(); } catch { }

            // Fire-and-forget cleanup (don’t block UI thread)
            if (task != null || cts != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (task != null)
                            await task.ConfigureAwait(false);
                    }
                    catch { /* swallow */ }
                    finally
                    {
                        try { cts?.Dispose(); } catch { }
                    }
                });
            }
        }

        // Remove expired users (those that haven't sent a heartbeat within the lease timeout)
        private void ReapExpiredUsers()
        {
            var now = DateTime.UtcNow;
            List<string>? expired = null;

            lock (_userPlansLock)
            {
                foreach (var kvp in _userPlans)
                {
                    if (now - kvp.Value.LastSeenUtc > _leaseTimeout)
                    {
                        expired ??= new List<string>();
                        expired.Add(kvp.Key);
                    }
                }

                if (expired != null)
                {
                    foreach (var id in expired)
                        _userPlans.Remove(id);
                }
            }

            if (expired == null || expired.Count == 0)
                return;

            _desiredPlan = ComputeUnionPlan();
            _ = ApplyPlanIfChangedAsync(CancellationToken.None);
        }

        private TelemetryPlan ComputeUnionPlan()
        {
            // Deterministic: union all, sort ascending, take max N
            static int[] UnionSortedCapped(IEnumerable<int> allIds, int cap)
            {
                return allIds
                    .Where(x => x > 0)
                    .Distinct()
                    .OrderBy(x => x)
                    .Take(cap)
                    .ToArray();
            }

            List<TelemetryPlan> plans;
            lock (_userPlansLock)
                plans = _userPlans.Values.Select(s => s.Plan).ToList();

            var di = UnionSortedCapped(plans.SelectMany(p => p.DI ?? System.Array.Empty<int>()), MaxDi);
            var gi = UnionSortedCapped(plans.SelectMany(p => p.GI ?? System.Array.Empty<int>()), MaxGi);
            var go = UnionSortedCapped(plans.SelectMany(p => p.GO ?? System.Array.Empty<int>()), MaxGo);

            return new TelemetryPlan(di, gi, go);
        }

        private static bool PlanEquals(TelemetryPlan a, TelemetryPlan b)
        {
            static bool SeqEq(IReadOnlyCollection<int> x, IReadOnlyCollection<int> y)
                => x.Count == y.Count && x.OrderBy(v => v).SequenceEqual(y.OrderBy(v => v));

            return SeqEq(a.DI, b.DI) && SeqEq(a.GI, b.GI) && SeqEq(a.GO, b.GO);
        }

        private async Task ApplyPlanIfChangedAsync(CancellationToken ct)
        {
            // Only KAREL supports PLAN right now
            if (_transport is not KarelTransport karel)
                return;

            var desired = _desiredPlan;
            if (PlanEquals(desired, _appliedPlan))
                return;

            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Re-check under lock (another apply might have won)
                desired = _desiredPlan;
                if (PlanEquals(desired, _appliedPlan))
                    return;

                // Apply each section. Empty list clears the plan on robot (PLAN_DI= with nothing)
                await SendPlanAsync(karel, "PLAN_DI", desired.DI, ct).ConfigureAwait(false);
                await SendPlanAsync(karel, "PLAN_GI", desired.GI, ct).ConfigureAwait(false);
                await SendPlanAsync(karel, "PLAN_GO", desired.GO, ct).ConfigureAwait(false);

                _appliedPlan = desired;

                Log?.Invoke($"Applied plan: DI[{desired.DI.Count}] GI[{desired.GI.Count}] GO[{desired.GO.Count}]");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static async Task SendPlanAsync(KarelTransport karel, string prefix, IReadOnlyCollection<int> ids, CancellationToken ct)
        {
            // Example: "PLAN_DI=105,230"
            var payload = ids.Count == 0
                ? $"{prefix}="
                : $"{prefix}={string.Join(",", ids)}";

            await karel.SendCommandAsync(payload, ct).ConfigureAwait(false);
            var resp = (await karel.ReadLineAsync(ct).ConfigureAwait(false)).Trim();

            if (!resp.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Robot rejected {prefix}: '{resp}'");
        }


        private async Task StopStreamingInternalAsync()
        {
            if (_streamCts == null) return;

            try { _streamCts.Cancel(); } catch { }

            try
            {
                if (_streamTask != null)
                    await _streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch { }

            try { _streamCts.Dispose(); } catch { }
            _streamCts = null;
            _streamTask = null;
        }

        private async Task StreamLoopAsync(int periodMs, CancellationToken ct)
        {
            if (_transport is not KarelTransport karel)
                throw new InvalidOperationException("Streaming loop currently implemented for KAREL only.");

            var next = DateTime.UtcNow;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    next = next.AddMilliseconds(periodMs);

                    using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    frameCts.CancelAfter(TimeSpan.FromMilliseconds(500));

                    await _ioLock.WaitAsync(frameCts.Token).ConfigureAwait(false);
                    try
                    {
                        await karel.SendCommandAsync("GET_FAST", frameCts.Token).ConfigureAwait(false);
                        var frame = await ReadFastFrameAsync(karel, frameCts.Token).ConfigureAwait(false);

                        TelemetryUpdated?.Invoke(frame);
                        if (frame.JointsDeg is not null)
                            JointsUpdated?.Invoke(frame.JointsDeg);
                    }
                    finally
                    {
                        _ioLock.Release();
                    }

                    var delay = next - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    else
                        next = DateTime.UtcNow;
                }

            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Frame timeout (likely missing END / stalled stream) -> treat as connection loss
                StatusChanged?.Invoke(RobotStatus.Disconnected, "Robot stream timed out");

                try { _streamCts?.Cancel(); } catch { }
                try { _streamCts?.Dispose(); } catch { }
                _streamCts = null;
                _streamTask = null;
                _connectionLostTcs?.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // normal stop
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is SocketException ||
                ex is ObjectDisposedException)
            {
                // Robot power-cycle / socket reset / server stopped
                StatusChanged?.Invoke(RobotStatus.Disconnected, "Robot connection lost");

                // Clear streaming state so it can restart after reconnect
                try { _streamCts?.Cancel(); } catch { }
                try { _streamCts?.Dispose(); } catch { }
                _streamCts = null;
                _streamTask = null;

                // Signal connection manager to reconnect now
                _connectionLostTcs?.TrySetResult(true);
            }
        }

private static async Task<TelemetryFrame> ReadFastFrameAsync(KarelTransport karel, CancellationToken ct)
    {
        double[]? joints = null;
        Dictionary<int, int>? di = null;
        Dictionary<int, int>? gi = null;
        Dictionary<int, int>? go = null;

        while (true)
        {
            var raw = await karel.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var line = raw.Trim();

            if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                return new TelemetryFrame(
                    Timestamp: DateTimeOffset.UtcNow,
                    JointsDeg: joints,
                    DI: di,
                    GI: gi,
                    GO: go
                );
            }

            // Joints line (tolerate prefix junk like "OJ=")
            var jIdx = line.IndexOf("J=", StringComparison.OrdinalIgnoreCase);

            if (jIdx >= 0)
            {
                var csv = line.Substring(jIdx + 2).Trim();
                var parts = csv.Split(',');

                if (parts.Length == 6)
                {
                    var arr = new double[6];
                    for (int i = 0; i < 6; i++)
                        arr[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);

                    joints = arr;
                }

                continue;
            }

            if (line.StartsWith("DI=", StringComparison.OrdinalIgnoreCase))
            {
                di ??= new Dictionary<int, int>();
                ParseIdValueListInto(line.AsSpan(3), di);
                continue;
            }

            if (line.StartsWith("GI=", StringComparison.OrdinalIgnoreCase))
            {
                gi ??= new Dictionary<int, int>();
                ParseIdValueListInto(line.AsSpan(3), gi);
                continue;
            }

            if (line.StartsWith("GO=", StringComparison.OrdinalIgnoreCase))
            {
                go ??= new Dictionary<int, int>();
                ParseIdValueListInto(line.AsSpan(3), go);
                continue;
            }

            // Ignore unknown lines
        }
    }

        private static void ParseIdValueListInto(ReadOnlySpan<char> s, Dictionary<int, int> dest)
        {
            // Format examples:
            // "105:0,106:1"
            // " 105: 0, 106: 1"
            if (s.IsEmpty) return;

            while (!s.IsEmpty)
            {
                // take token up to comma
                int comma = s.IndexOf(',');
                ReadOnlySpan<char> token = comma >= 0 ? s[..comma] : s;
                token = token.Trim();
                if (comma >= 0) s = s[(comma + 1)..];
                else s = ReadOnlySpan<char>.Empty;

                if (token.IsEmpty) continue;

                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1) continue;

                var idSpan = token[..colon].Trim();
                var valSpan = token[(colon + 1)..].Trim();

                if (!int.TryParse(idSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;

                if (!int.TryParse(valSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                    continue;

                dest[id] = val;
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            int attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    StatusChanged?.Invoke(RobotStatus.Connecting, null);

                    _connectionLostTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    await DisposeTransportQuietlyAsync().ConfigureAwait(false);

                    _transport = _transportFactory();
                    await _transport.ConnectAsync(_config, ct).ConfigureAwait(false);

                    // After reconnect, robot plan resets (per-session), so re-apply union plan
                    _appliedPlan = TelemetryPlan.Empty;
                    _desiredPlan = ComputeUnionPlan();
                    if (_transport is KarelTransport)
                        await ApplyPlanIfChangedAsync(ct).ConfigureAwait(false);

                    StatusChanged?.Invoke(RobotStatus.Connected, null);
                    _connectedTcs?.TrySetResult(true);
                    attempt = 0;

                    // Auto-start streaming if desired
                    if (_desiredStreaming && _streamCts == null)
                        _ = StartStreamingAsync(_streamPeriodMs);

                    while (!ct.IsCancellationRequested)
                    {
                        var delayTask = Task.Delay(1000, ct);
                        var lostTask = _connectionLostTcs.Task;

                        var completed = await Task.WhenAny(delayTask, lostTask).ConfigureAwait(false);

                        if (completed == lostTask)
                            throw new IOException("Connection lost");

                        // If streaming is desired but stopped, restart it
                        if (_desiredStreaming && _streamCts == null && _transport != null)
                            _ = StartStreamingAsync(_streamPeriodMs);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    StatusChanged?.Invoke(RobotStatus.Disconnected, "Reconnecting...");

                    await StopStreamingInternalAsync().ConfigureAwait(false);
                    await DisposeTransportQuietlyAsync().ConfigureAwait(false);

                    attempt++;
                    var delayMs = Math.Min(10_000, 500 * attempt);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task DisposeTransportQuietlyAsync()
        {
            if (_transport == null) return;

            try { await _transport.DisposeAsync().ConfigureAwait(false); }
            catch { }
            finally { _transport = null; }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }
}
