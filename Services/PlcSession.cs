// =====================================================
// COMPLETE REPLACEMENT: Services/PlcSession.cs
// Uses DeviceSessionBase<PlcFrame> correctly (no duplicate members)
// =====================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    /// <summary>
    /// PLC session (ControlLogix) built on DeviceSessionBase:
    /// - Connect/Disconnect handled by base
    /// - Base RunLoop calls ReadFrameAsync continuously
    /// - We gate reading by publishAllowed (base default) to match "has users"
    /// - Lease reaper + union plan live here
    /// </summary>
    public sealed class PlcSession : DeviceSessionBase<PlcFrame>, IPlanTarget, IMachineDataPlanTarget
    {
        private sealed class UserPlanState
        {
            public MachineDataPlan Plan { get; set; } = new MachineDataPlan(Array.Empty<MachineDataPlanItem>());
            public DateTime LastSeenUtc { get; set; }
        }

        public event Action<string>? Log;

        private readonly PlcConfig _config;
        private readonly Func<IPlcTransport> _transportFactory;
        private IPlcTransport? _transport;

        private readonly object _userPlansLock = new();
        private readonly Dictionary<string, UserPlanState> _userPlans = new(StringComparer.Ordinal);

        private readonly TimeSpan _leaseTimeout = TimeSpan.FromSeconds(60);
        private readonly TimeSpan _reapInterval = TimeSpan.FromSeconds(5);
        private CancellationTokenSource? _leaseCts;
        private Task? _leaseTask;

        private long _frameSeq;

        // Current union plan snapshot (recomputed when plans change)
        private volatile MachineDataPlanItem[] _unionItems = Array.Empty<MachineDataPlanItem>();

        // Optional: remember desired period from plan messages (used by client/UI)
        private volatile int _periodMs;

        public PlcSession(
            string tenantId,
            string gatewayId,
            PlcConfig config,
            Func<IPlcTransport> transportFactory)
            : base(new DeviceKey(tenantId, gatewayId, config.Name, "plc-ab"))
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));

            _periodMs = Math.Max(50, _config.DefaultPeriodMs);
        }

        // ---------------------------
        // IPlanTarget (required)
        // ---------------------------
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

            RecomputeUnionAndDemand();
        }

        // Robot-only in interface; PLC ignores it.
        public void ApplyTelemetryPlan(string userId, TelemetryPlan plan, int? periodMs)
        {
            // No-op for PLC.
        }

        // ---------------------------
        // IMachineDataPlanTarget (PLC)
        // ---------------------------
        public void ApplyMachineDataPlan(string userId, MachineDataPlan plan, int? periodMs)
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

            if (periodMs.HasValue && periodMs.Value > 0)
                _periodMs = Math.Max(50, periodMs.Value);

            RecomputeUnionAndDemand();
        }

        // ---------------------------
        // DeviceSessionBase hooks
        // ---------------------------
        protected override async Task OnConnectAsync(CancellationToken ct)
        {
            // Establish transport connection
            _transport = _transportFactory();
            await _transport.ConnectAsync(_config, ct).ConfigureAwait(false);

            StartLeaseReaper();

            // Ensure demand state is correct on connect
            RecomputeUnionAndDemand();

            Log?.Invoke($"[PLC] Connected {Key}");
        }

        protected override async Task OnDisconnectAsync(CancellationToken ct)
        {
            StopLeaseReaper();

            if (_transport != null)
            {
                try { await _transport.DisconnectAsync(ct).ConfigureAwait(false); } catch { }
                try { await _transport.DisposeAsync().ConfigureAwait(false); } catch { }
                _transport = null;
            }

            Log?.Invoke($"[PLC] Disconnected {Key}");
        }

        protected override async Task<PlcFrame> ReadFrameAsync(CancellationToken ct)
        {
            // Base already gates calls to ReadFrameAsync when publish is not allowed
            // (ReadOnlyWhenPublishAllowed == true), so at this point we can assume:
            // - there are active users
            // - publishAllowed == true

            var t = _transport;
            if (t == null)
                throw new InvalidOperationException("PLC transport is not connected.");

            // Snapshot union items computed from user plans
            var items = Volatile.Read(ref _unionItems);
            if (items.Length == 0)
            {
                // Users may exist but items are empty; idle briefly to avoid spin.
                await Task.Delay(50, ct).ConfigureAwait(false);
                return new PlcFrame(DateTimeOffset.UtcNow, Interlocked.Increment(ref _frameSeq), new Dictionary<string, PlcValue>());
            }

            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            frameCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(200, _config.TimeoutMs)));

            var values = await t.ReadAsync(items, _config, frameCts.Token).ConfigureAwait(false);

            // Respect plan period (soft pacing). Base loop is "as fast as ReadFrameAsync returns",
            // so we enforce pacing here.
            var delay = _periodMs;
            if (delay > 0)
                await Task.Delay(delay, ct).ConfigureAwait(false);

            var seq = Interlocked.Increment(ref _frameSeq);
            return new PlcFrame(DateTimeOffset.UtcNow, seq, values);
        }

        // ---------------------------
        // Lease reaper
        // ---------------------------
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
                    try { if (task != null) await task.ConfigureAwait(false); }
                    catch { }
                    finally { try { cts?.Dispose(); } catch { } }
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

            if (expired == null || expired.Count == 0) return;

            RecomputeUnionAndDemand();
        }

        // ---------------------------
        // Union + demand gating
        // ---------------------------
        private void RecomputeUnionAndDemand()
        {
            var union = ComputeUnionItems();
            Volatile.Write(ref _unionItems, union);

            var hasUsers = HasAnyActiveUsers();

            // publishAllowed should follow hasUsers
            // base RunLoop will only call ReadFrameAsync when publishAllowed is true
            SetPublishAllowed(hasUsers);

            Log?.Invoke($"[PLC] users={(_userPlans.Count)} items={union.Length} publishAllowed={hasUsers}");
        }

        private bool HasAnyActiveUsers()
        {
            lock (_userPlansLock)
                return _userPlans.Count > 0;
        }

        private MachineDataPlanItem[] ComputeUnionItems()
        {
            List<MachineDataPlanItem> all;

            lock (_userPlansLock)
            {
                all = _userPlans.Values
                    .SelectMany(s => s.Plan.Items ?? Array.Empty<MachineDataPlanItem>())
                    .ToList();
            }

            var max = _config.MaxItems <= 0 ? 50 : _config.MaxItems;

            var uniq = all
                .Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .Select(i => new MachineDataPlanItem(i.Path.Trim(), i.Expand?.Trim()))
                .GroupBy(i => (Path: i.Path, Expand: i.Expand ?? string.Empty), StringTupleComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Expand ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToArray();

            return uniq;
        }

        private sealed class StringTupleComparer : IEqualityComparer<(string Path, string Expand)>
        {
            public static readonly StringTupleComparer OrdinalIgnoreCase = new();

            public bool Equals((string Path, string Expand) x, (string Path, string Expand) y)
                => StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path)
                   && StringComparer.OrdinalIgnoreCase.Equals(x.Expand, y.Expand);

            public int GetHashCode((string Path, string Expand) obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Expand));
        }
    }
}
