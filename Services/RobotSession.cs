// RobotSession.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    /// <summary>
    /// Manages a single robot connection + streaming loop.
    /// - Auto-reconnect ON by default
    /// - Demand-driven streaming: stream only when users exist
    /// - Uses END-framed GET_FAST responses to avoid backlog/delay
    /// </summary>
    public sealed class RobotSession : IDeviceSession<TelemetryFrame>, IPlanTarget
    {
        private sealed class UserPlanState
        {
            public TelemetryPlan Plan { get; set; } = TelemetryPlan.Empty;
            public DateTime LastSeenUtc { get; set; }
        }

        public event Action<string>? Log;

        // Lease-based cleanup (optional)
        private readonly TimeSpan _leaseTimeout = TimeSpan.FromSeconds(60);
        private readonly TimeSpan _reapInterval = TimeSpan.FromSeconds(5);
        private CancellationTokenSource? _leaseCts;
        private Task? _leaseTask;

        private readonly SemaphoreSlim _ioLock = new(1, 1);

        // User plans (union model)
        private readonly object _userPlansLock = new();
        private readonly Dictionary<string, UserPlanState> _userPlans = new();

        private TelemetryPlan _appliedPlan = TelemetryPlan.Empty;
        private TelemetryPlan _desiredPlan = TelemetryPlan.Empty;

        // KAREL caps (must match GW_SERV5)
        private const int MaxDi = 10;
        private const int MaxGi = 10;
        private const int MaxGo = 10;
        private const int MaxDo = 10;
        private const int MaxR  = 10;
        private const int MaxVar = 10;

        private readonly RobotConfig _config;
        private readonly Func<IRobotTransport> _transportFactory;

        private IRobotTransport? _transport;

        private CancellationTokenSource? _cts;
        private Task? _runTask;

        private CancellationTokenSource? _streamCts;
        private Task? _streamTask;

        private TaskCompletionSource<bool>? _connectedTcs;
        private TaskCompletionSource<bool>? _connectionLostTcs;

        // demand-driven: stream only when users exist
        private volatile bool _desiredStreaming = false;
        private volatile int _streamPeriodMs = 30;

        // frame sequence (session-local)
        private long _frameSeq;

        public DeviceKey Key { get; }

        // ---- Multi-device surface (IDeviceSession<TelemetryFrame>) ----
        public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;

        public event Action<DeviceStatus, Exception?>? StatusChanged;
        public event Action<TelemetryFrame>? FrameReceived;
        public event Action<bool>? PublishAllowedChanged;

        private bool _publishAllowed;

        public void SetPublishAllowed(bool allowed)
        {
            if (_publishAllowed == allowed) return;
            _publishAllowed = allowed;
            PublishAllowedChanged?.Invoke(allowed);
        }

        // ---- Existing robot/UI surface you already use ----
        public event Action<RobotStatus, string?>? RobotStatusChanged;
        public event Action<double[]>? JointsUpdated;
        public event Action<TelemetryFrame>? TelemetryUpdated;
        public event Action<bool>? ActiveUsersChanged;

        public RobotSession(RobotConfig config, Func<IRobotTransport> transportFactory)
            : this(
                tenantId: "default",
                gatewayId: "TwinSyncGateway-001",
                robotName: config?.Name ?? "Robot",
                config: config,
                transportFactory: transportFactory)
        {
        }

        public RobotSession(
            string tenantId,
            string gatewayId,
            string robotName,
            RobotConfig config,
            Func<IRobotTransport> transportFactory)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));

            Key = new DeviceKey(
                tenantId,
                gatewayId,
                robotName,
                "robot-fanuc");
        }

        // ---- IPlanTarget ----
        public void ApplyTelemetryPlan(string userId, TelemetryPlan plan, int? periodMs)
        {
            SetUserPlan(userId, plan);

            if (periodMs.HasValue && periodMs.Value > 0)
                _ = StartStreamingAsync(periodMs.Value);
        }

        public bool HasAnyActiveUsers()
        {
            lock (_userPlansLock)
                return _userPlans.Count > 0;
        }

        private void UpdateStreamingDemand()
        {
            var want = HasAnyActiveUsers();

            if (_desiredStreaming == want) return;

            _desiredStreaming = want;
            ActiveUsersChanged?.Invoke(want);

            // Cloud publish gating should always match "has users"
            SetPublishAllowed(want);

            // Start/stop streaming without tearing down the socket
            if (want)
                _ = StartStreamingAsync(_streamPeriodMs);
            else
                _ = StopStreamingInternalAsync();
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
            UpdateStreamingDemand();

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
            UpdateStreamingDemand();

            _ = ApplyPlanIfChangedAsync(CancellationToken.None);
        }

        public TelemetryPlan GetDesiredPlan() => _desiredPlan;
        public TelemetryPlan GetAppliedPlan() => _appliedPlan;

        // IDeviceSession interface signature (no CT)
        public Task ConnectAsync() => ConnectAsync(CancellationToken.None);

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_cts != null) return;

            _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runTask = Task.Run(() => RunAsync(_cts.Token));

            await _connectedTcs.Task.ConfigureAwait(false);

            StartLeaseReaper();

            // Ensure publishAllowed reflects current user state on connect
            SetPublishAllowed(HasAnyActiveUsers());

            // If users already exist, streaming will start
            UpdateStreamingDemand();
        }

        public async Task DisconnectAsync()
        {
            // 🔒 critical invariants
            SetPublishAllowed(false);
            _desiredStreaming = false;

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
            catch { }

            try { _cts.Dispose(); } catch { }
            _cts = null;
            _runTask = null;
            _connectedTcs = null;
            _connectionLostTcs = null;

            SetDeviceStatus(DeviceStatus.Disconnected, null);
            RobotStatusChanged?.Invoke(RobotStatus.Disconnected, null);
        }

        public Task StartStreamingAsync(int periodMs = 30)
        {
            _desiredStreaming = true;
            _streamPeriodMs = periodMs;

            if (_transport == null)
                return Task.CompletedTask;

            if (_streamCts != null)
                return Task.CompletedTask;

            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => StreamLoopAsync(periodMs, _streamCts.Token));

            // Streaming state
            SetDeviceStatus(DeviceStatus.Streaming, null);
            RobotStatusChanged?.Invoke(RobotStatus.Streaming, null);

            return Task.CompletedTask;
        }

        public async Task StopStreamingAsync()
        {
            _desiredStreaming = false;
            await StopStreamingInternalAsync().ConfigureAwait(false);
        }

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
                    catch { }
                }
            }, ct);
        }

        private void StopLeaseReaper()
        {
            var cts = _leaseCts;
            var task = _leaseTask;

            _leaseCts = null;
            _leaseTask = null;

            try { cts?.Cancel(); } catch { }

            if (task != null || cts != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (task != null)
                            await task.ConfigureAwait(false);
                    }
                    catch { }
                    finally
                    {
                        try { cts?.Dispose(); } catch { }
                    }
                });
            }
        }

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
            UpdateStreamingDemand();
            _ = ApplyPlanIfChangedAsync(CancellationToken.None);
        }

        private TelemetryPlan ComputeUnionPlan()
        {
            static int[] UnionSortedCapped(IEnumerable<int> allIds, int cap)
            {
                return allIds
                    .Where(x => x > 0)
                    .Distinct()
                    .OrderBy(x => x)
                    .Take(cap)
                    .ToArray();
            }

            static string[] UnionSortedCappedStrings(IEnumerable<string> all, int cap)
            {
                return all
                    .Select(s => (s ?? string.Empty).Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .Take(cap)
                    .ToArray();
            }

            List<TelemetryPlan> plans;
            lock (_userPlansLock)
                plans = _userPlans.Values.Select(s => s.Plan).ToList();

            var di = UnionSortedCapped(plans.SelectMany(p => p.DI ?? Array.Empty<int>()), MaxDi);
            var gi = UnionSortedCapped(plans.SelectMany(p => p.GI ?? Array.Empty<int>()), MaxGi);
            var go = UnionSortedCapped(plans.SelectMany(p => p.GO ?? Array.Empty<int>()), MaxGo);

            var dO = UnionSortedCapped(plans.SelectMany(p => p.DO ?? Array.Empty<int>()), MaxDo);
            var r  = UnionSortedCapped(plans.SelectMany(p => p.R ?? Array.Empty<int>()), MaxR);
            var vars = UnionSortedCappedStrings(plans.SelectMany(p => p.VAR ?? Array.Empty<string>()), MaxVar);

            return new TelemetryPlan(di, gi, go, dO, r, vars);
        }

        private static bool PlanEquals(TelemetryPlan a, TelemetryPlan b)
        {
            static bool SeqEq(IReadOnlyCollection<int> x, IReadOnlyCollection<int> y)
                => x.Count == y.Count && x.OrderBy(v => v).SequenceEqual(y.OrderBy(v => v));

            static bool SeqEqStr(IReadOnlyCollection<string> x, IReadOnlyCollection<string> y)
                => x.Count == y.Count &&
                   x.Select(s => (s ?? string.Empty).Trim()).OrderBy(v => v, StringComparer.Ordinal)
                    .SequenceEqual(y.Select(s => (s ?? string.Empty).Trim()).OrderBy(v => v, StringComparer.Ordinal));

            return SeqEq(a.DI, b.DI) &&
                   SeqEq(a.GI, b.GI) &&
                   SeqEq(a.GO, b.GO) &&
                   SeqEq(a.DO, b.DO) &&
                   SeqEq(a.R, b.R) &&
                   SeqEqStr(a.VAR, b.VAR);
        }

        private async Task ApplyPlanIfChangedAsync(CancellationToken ct)
        {
            if (_transport is not KarelTransport karel)
                return;

            var desired = _desiredPlan;
            if (PlanEquals(desired, _appliedPlan))
                return;

            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                desired = _desiredPlan;
                if (PlanEquals(desired, _appliedPlan))
                    return;

                await SendPlanAsync(karel, "PLAN_DI", desired.DI, ct).ConfigureAwait(false);
                await SendPlanAsync(karel, "PLAN_GI", desired.GI, ct).ConfigureAwait(false);
                await SendPlanAsync(karel, "PLAN_GO", desired.GO, ct).ConfigureAwait(false);
                await SendPlanAsync(karel, "PLAN_DO", desired.DO, ct).ConfigureAwait(false);
                await SendPlanAsync(karel, "PLAN_R",  desired.R,  ct).ConfigureAwait(false);
                await SendPlanVarsAsync(karel, desired.VAR, ct).ConfigureAwait(false);

                _appliedPlan = desired;

                Log?.Invoke($"Applied plan: DI[{desired.DI.Count}] GI[{desired.GI.Count}] GO[{desired.GO.Count}] DO[{desired.DO.Count}] R[{desired.R.Count}] VAR[{desired.VAR.Count}]");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static async Task SendPlanAsync(KarelTransport karel, string prefix, IReadOnlyCollection<int> ids, CancellationToken ct)
        {
            var payload = ids.Count == 0 ? $"{prefix}=" : $"{prefix}={string.Join(",", ids)}";

            await karel.SendCommandAsync(payload, ct).ConfigureAwait(false);
            var resp = (await karel.ReadLineAsync(ct).ConfigureAwait(false)).Trim();

            if (!resp.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Robot rejected {prefix}: '{resp}'");
        }

        private static async Task SendPlanVarsAsync(KarelTransport karel, IReadOnlyCollection<string> vars, CancellationToken ct)
        {
            // KAREL side expects: PLAN_VAR=name1,name2,... (no quoting/escaping)
            var cleaned = vars
                .Select(v => (v ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .ToArray();

            var payload = cleaned.Length == 0
                ? "PLAN_VAR="
                : $"PLAN_VAR={string.Join(",", cleaned)}";

            await karel.SendCommandAsync(payload, ct).ConfigureAwait(false);
            var resp = (await karel.ReadLineAsync(ct).ConfigureAwait(false)).Trim();

            if (!resp.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Robot rejected PLAN_VAR: '{resp}'");
        }

        private async Task StopStreamingInternalAsync()
        {
            // Atomically take ownership of the CTS/task
            var cts = Interlocked.Exchange(ref _streamCts, null);
            var task = Interlocked.Exchange(ref _streamTask, null);

            if (cts == null) return; // someone else already stopped it

            try { cts.Cancel(); } catch { }

            try
            {
                if (task != null)
                    await task.ConfigureAwait(false);
            }
            catch { /* swallow */ }

            try { cts.Dispose(); } catch { }
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

                        // Multi-device surface
                        FrameReceived?.Invoke(frame);

                        // Existing surface
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
                // frame timeout: treat as connection loss signal
                SetDeviceStatus(DeviceStatus.Faulted, new TimeoutException("Robot stream timed out"));
                RobotStatusChanged?.Invoke(RobotStatus.Disconnected, "Robot stream timed out");
                _connectionLostTcs?.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // normal stop
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
            {
                SetDeviceStatus(DeviceStatus.Faulted, ex);
                RobotStatusChanged?.Invoke(RobotStatus.Disconnected, "Robot connection lost");
                _connectionLostTcs?.TrySetResult(true);
            }
            finally
            {
                // Always clear streaming state so reconnect can restart it
                try { _streamCts?.Dispose(); } catch { }
                _streamCts = null;
                _streamTask = null;
            }
        }

        private async Task<TelemetryFrame> ReadFastFrameAsync(KarelTransport karel, CancellationToken ct)
        {
            double[]? joints = null;
            Dictionary<int, int>? di = null;
            Dictionary<int, int>? gi = null;
            Dictionary<int, int>? go = null;
            Dictionary<int, int>? dO = null;
            Dictionary<int, TelemetryRegisterValue>? r = null;
            Dictionary<string, string>? vars = null;

            while (true)
            {
                var raw = await karel.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var line = raw.Trim();

                if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    var seq = Interlocked.Increment(ref _frameSeq);

                    return new TelemetryFrame(
                        Timestamp: DateTimeOffset.UtcNow,
                        Sequence: seq,
                        JointsDeg: joints,
                        DI: di,
                        GI: gi,
                        GO: go,
                        DO: dO,
                        R: r,
                        VAR: vars
                    );
                }

                var jIdx = line.IndexOf("J=", StringComparison.OrdinalIgnoreCase);
                if (jIdx >= 0)
                {
                    var csv = line[(jIdx + 2)..].Trim();
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

                if (line.StartsWith("DO=", StringComparison.OrdinalIgnoreCase))
                {
                    dO ??= new Dictionary<int, int>();
                    ParseIdValueListInto(line.AsSpan(3), dO);
                    continue;
                }

                if (line.StartsWith("R=", StringComparison.OrdinalIgnoreCase))
                {
                    r ??= new Dictionary<int, TelemetryRegisterValue>();
                    ParseRegisterListInto(line.AsSpan(2), r);
                    continue;
                }

                if (line.StartsWith("VAR=", StringComparison.OrdinalIgnoreCase))
                {
                    vars ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    ParseVarListInto(line.AsSpan(4), vars);
                    continue;
                }
            }
        }

        private static void ParseIdValueListInto(ReadOnlySpan<char> s, Dictionary<int, int> dest)
        {
            if (s.IsEmpty) return;

            while (!s.IsEmpty)
            {
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

        private static void ParseRegisterListInto(ReadOnlySpan<char> s, Dictionary<int, TelemetryRegisterValue> dest)
        {
            // Format: "12:123|123.000,5:0|0.000" or "12:ERR" (ignored)
            if (s.IsEmpty) return;

            while (!s.IsEmpty)
            {
                int comma = s.IndexOf(',');
                ReadOnlySpan<char> token = comma >= 0 ? s[..comma] : s;
                token = token.Trim();

                if (comma >= 0) s = s[(comma + 1)..];
                else s = ReadOnlySpan<char>.Empty;

                if (token.IsEmpty) continue;

                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1) continue;

                var idSpan = token[..colon].Trim();
                var rest = token[(colon + 1)..].Trim();

                if (!int.TryParse(idSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;

                // rest is either "ERR" or "int|real"
                if (rest.Equals("ERR".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    continue;

                int pipe = rest.IndexOf('|');
                if (pipe <= 0 || pipe >= rest.Length - 1) continue;

                var intSpan = rest[..pipe].Trim();
                var realSpan = rest[(pipe + 1)..].Trim();

                int.TryParse(intSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iVal);
                double.TryParse(realSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var rVal);

                dest[id] = new TelemetryRegisterValue(iVal, rVal);
            }
        }

        private static void ParseVarListInto(ReadOnlySpan<char> s, Dictionary<string, string> dest)
        {
            // Format: "$FOO:R:12.500,$BAR:ERR"  (value stored as "R:12.500" or "ERR")
            if (s.IsEmpty) return;

            while (!s.IsEmpty)
            {
                int comma = s.IndexOf(',');
                ReadOnlySpan<char> token = comma >= 0 ? s[..comma] : s;
                token = token.Trim();

                if (comma >= 0) s = s[(comma + 1)..];
                else s = ReadOnlySpan<char>.Empty;

                if (token.IsEmpty) continue;

                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1) continue;

                var nameSpan = token[..colon].Trim();
                var rest = token[(colon + 1)..].Trim();
                if (nameSpan.IsEmpty) continue;

                dest[nameSpan.ToString()] = rest.ToString();
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            int attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetDeviceStatus(DeviceStatus.Connecting, null);
                    RobotStatusChanged?.Invoke(RobotStatus.Connecting, null);

                    _connectionLostTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    await DisposeTransportQuietlyAsync().ConfigureAwait(false);

                    _transport = _transportFactory();
                    await _transport.ConnectAsync(_config, ct).ConfigureAwait(false);

                    // Re-apply union plan after reconnect
                    _appliedPlan = TelemetryPlan.Empty;
                    _desiredPlan = ComputeUnionPlan();
                    if (_transport is KarelTransport)
                        await ApplyPlanIfChangedAsync(ct).ConfigureAwait(false);

                    SetDeviceStatus(DeviceStatus.Connected, null);
                    RobotStatusChanged?.Invoke(RobotStatus.Connected, null);

                    _connectedTcs?.TrySetResult(true);
                    attempt = 0;

                    // Start streaming only if desired and users exist
                    if (_desiredStreaming && _streamCts == null && HasAnyActiveUsers())
                        _ = StartStreamingAsync(_streamPeriodMs);

                    while (!ct.IsCancellationRequested)
                    {
                        var delayTask = Task.Delay(1000, ct);
                        var lostTask = _connectionLostTcs.Task;

                        var completed = await Task.WhenAny(delayTask, lostTask).ConfigureAwait(false);

                        if (completed == lostTask)
                            throw new IOException("Connection lost");

                        if (_desiredStreaming && _streamCts == null && _transport != null && HasAnyActiveUsers())
                            _ = StartStreamingAsync(_streamPeriodMs);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // reconnect backoff
                    SetDeviceStatus(DeviceStatus.Faulted, ex);
                    RobotStatusChanged?.Invoke(RobotStatus.Disconnected, "Reconnecting...");

                    await StopStreamingInternalAsync().ConfigureAwait(false);
                    await DisposeTransportQuietlyAsync().ConfigureAwait(false);

                    attempt++;
                    var delayMs = Math.Min(10_000, 500 * attempt);
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
        }

        private void SetDeviceStatus(DeviceStatus s, Exception? ex)
        {
            Status = s;
            StatusChanged?.Invoke(s, ex);
        }

        private async Task DisposeTransportQuietlyAsync()
        {
            if (_transport == null) return;

            try { await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
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
