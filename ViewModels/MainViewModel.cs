using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TwinSync_Gateway.Models;
using TwinSync_Gateway.Services;

namespace TwinSync_Gateway.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        // Gateway identity (until moved into config)
        private const string TenantId = "default";
        private const string GatewayId = "TwinSyncGateway-001";

        private IotMqttConnection? _mqtt;
        private IotPlanIngress? _iotIngress;
        private IotDataEgress? _iotEgress;
        private IotRosterPublisher? _rosterPublisher;

        private readonly RobotConfigStore _robotStore = new RobotConfigStore();

        // PLC (headless) config + sessions
        private readonly PlcConfigStore _plcStore = new PlcConfigStore();
        private readonly List<PlcConfig> _plcConfigs = new();
        private readonly List<PlcSession> _plcSessions = new();
        private readonly Dictionary<DeviceKey, DateTimeOffset> _plcLastFrameAt = new();

        private bool _useSimPlc = false; // flip to false for real PLC

        // Central ingress routing registry (FULL DeviceKey)
        private readonly Dictionary<DeviceKey, IPlanTarget> _targets = new();

        public ObservableCollection<RobotConfigViewModel> Robots { get; } = new();

        private RobotConfigViewModel? _selectedRobot;
        public RobotConfigViewModel? SelectedRobot
        {
            get => _selectedRobot;
            set
            {
                if (Set(ref _selectedRobot, value))
                {
                    RemoveRobotCommand.RaiseCanExecuteChanged();
                    ConnectCommand.RaiseCanExecuteChanged();
                    DisconnectCommand.RaiseCanExecuteChanged();
                    ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                    ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                    RemoveUserBCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public RelayCommand AddRobotCommand { get; }
        public RelayCommand RemoveRobotCommand { get; }
        public RelayCommand SaveCommand { get; }
        public RelayCommand ReloadCommand { get; }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand DisconnectCommand { get; }

        // Temp debugging (robot-only)
        public RelayCommand ApplyUserAPlanCommand { get; }
        public RelayCommand ApplyUserBPlanCommand { get; }
        public RelayCommand RemoveUserBCommand { get; }

        public MainViewModel()
        {
            ApplyUserAPlanCommand = new RelayCommand(ApplyUserAPlan, CanPlanSelected);
            ApplyUserBPlanCommand = new RelayCommand(ApplyUserBPlan, CanPlanSelected);
            RemoveUserBCommand = new RelayCommand(RemoveUserB, CanPlanSelected);

            AddRobotCommand = new RelayCommand(AddRobot);
            RemoveRobotCommand = new RelayCommand(RemoveSelectedRobot, () => SelectedRobot != null);
            SaveCommand = new RelayCommand(Save);
            ReloadCommand = new RelayCommand(Reload);

            ConnectCommand = new RelayCommand(ConnectSelected, CanConnectSelected);
            DisconnectCommand = new RelayCommand(DisconnectSelected, CanDisconnectSelected);

            Reload();
        }

        private bool CanPlanSelected()
            => SelectedRobot?.Session != null && SelectedRobot.Status != RobotStatus.Disconnected;

        private bool CanConnectSelected()
            => SelectedRobot != null && SelectedRobot.Status == RobotStatus.Disconnected;

        private bool CanDisconnectSelected()
            => SelectedRobot != null && SelectedRobot.Status != RobotStatus.Disconnected;

        // -------------------------------
        // IoT Start/Stop (called by App)
        // -------------------------------
        public async Task StartIotAsync()
        {
            if (_mqtt != null) return;

            // TODO: move these to config (do NOT hardcode secrets)
            var endpointHost = "a2oo54s3mt6et0-ats.iot.us-east-1.amazonaws.com";
            var mqttClientId = GatewayId;

            // ⚠️ Replace these with your actual path/password mechanism
            var cert = LoadClientCertFromPfx(
                pfxPath: @"C:\Users\jcollison\source\TwinSync_cert\TwinSyncGateway-001.pfx",
                password: "321cba"
            );

            _mqtt = new IotMqttConnection();
            _mqtt.Log += msg => System.Diagnostics.Debug.WriteLine($"[MQTT] {msg}");

            await _mqtt.ConnectAsync(endpointHost, 8883, mqttClientId, cert, CancellationToken.None);

            _rosterPublisher = new IotRosterPublisher(_mqtt, TenantId, GatewayId);

            // ✅ Egress (multi-device only)
            _iotEgress = new IotDataEgress(_mqtt, TenantId, GatewayId);
            _iotEgress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT-E] {msg}");
            _iotEgress.Start(TimeSpan.FromMilliseconds(30)); // publish tick

            // ✅ Start PLCs headless (no UI)
            LoadPlcConfigs();
            await StartPlcSessionsHeadlessAsync();

            // ✅ Build central target registry (PLCs are always present; robots register on connect)
            RebuildTargetsRegistry();

            // ✅ Multi-device ingress routing using FULL DeviceKey
            _iotIngress = new IotPlanIngress(
                mqtt: _mqtt,
                tenantId: TenantId,
                gatewayId: GatewayId,
                getTargetByKey: key =>
                {
                    // FULL key match: tenant/gateway/type/id
                    return _targets.TryGetValue(key, out var t) ? t : null;
                });

            _iotIngress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT] {msg}");
            await _iotIngress.SubscribeAsync(CancellationToken.None);

            await PublishDevicesRosterAsync();
        }

        public async Task StopIotAsync()
        {
            // Stop PLC sessions
            try
            {
                foreach (var plc in _plcSessions.ToArray())
                {
                    try { await plc.DisconnectAsync(); } catch { }
                    try { await plc.DisposeAsync(); } catch { }
                }
            }
            catch { }

            _plcSessions.Clear();
            _plcLastFrameAt.Clear();
            _targets.Clear();

            if (_iotEgress != null)
            {
                try { _iotEgress.ClearAll(); } catch { }
                try { await _iotEgress.DisposeAsync(); } catch { }
                _iotEgress = null;
            }

            _iotIngress = null;

            if (_mqtt != null)
            {
                try { await _mqtt.DisposeAsync(); } catch { }
                _mqtt = null;
            }

            System.Diagnostics.Debug.WriteLine("[IoT] Stopped IoT connection.");
        }

        // -------------------------------
        // PLC headless lifecycle
        // -------------------------------
        private void LoadPlcConfigs()
        {
            _plcConfigs.Clear();
            _plcConfigs.AddRange(_plcStore.Load());
        }

        private async Task StartPlcSessionsHeadlessAsync()
        {
            if (_iotEgress == null)
            {
                System.Diagnostics.Debug.WriteLine("[PLC] IoT egress not started; PLC frames will not publish.");
            }

            // If we already started PLC sessions, don’t duplicate
            if (_plcSessions.Count > 0) return;

            foreach (var cfg in _plcConfigs)
            {
                // For Phase 1: use simulator transport so end-to-end works before libplctag
                var session = new PlcSession(
                    tenantId: TenantId,
                    gatewayId: GatewayId,
                    config: cfg,
                    transportFactory: () => _useSimPlc ? new SimPlcTransport() : new LibPlcTagTransport());


                WirePlcSession(session);

                _plcSessions.Add(session);

                // headless connect
                try
                {
                    await session.ConnectAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PLC] Connect failed '{cfg.Name}': {ex.Message}");
                    // Keep session present so plans can still be accepted; roster will show status
                }
            }
        }

        private void WirePlcSession(PlcSession session)
        {
            var key = session.Key;

            // Publish gating: session raises PublishAllowedChanged; egress enforces invariant (disable clears cache)
            session.PublishAllowedChanged += allowed =>
            {
                try { _iotEgress?.SetPublishAllowed(key, allowed); } catch { }
            };

            // Frames
            session.FrameReceived += frame =>
            {
                try { _iotEgress?.Enqueue(key, frame); } catch { }

                lock (_plcLastFrameAt)
                    _plcLastFrameAt[key] = frame.Timestamp;
            };

            // Status -> roster + optional hardening
            session.StatusChanged += (status, ex) =>
            {
                if (status == DeviceStatus.Disconnected || status == DeviceStatus.Faulted)
                {
                    try { _iotEgress?.SetPublishAllowed(key, false); } catch { }
                }

                _ = PublishDevicesRosterAsync();
            };
        }

        // -------------------------------
        // Central registry / ingress routing
        // -------------------------------
        private void RebuildTargetsRegistry()
        {
            _targets.Clear();

            // PLCs: always registered (so plans can be applied even if disconnected)
            foreach (var plc in _plcSessions)
                _targets[plc.Key] = plc;

            // Robots: only registered when session exists (connected)
            foreach (var r in Robots)
            {
                if (r.Session is IPlanTarget t)
                    _targets[t.Key] = t;
            }
        }

        // -------------------------------
        // Devices roster (robots + PLCs)
        // -------------------------------
        private Task PublishDevicesRosterAsync()
        {
            if (_rosterPublisher == null) return Task.CompletedTask;

            // Snapshot on UI dispatcher for Robots collection safety
            var robotsSnapshot =
                Application.Current.Dispatcher.Invoke(() =>
                    Robots.Select(r =>
                    {
                        // For disconnected robots, we still publish them as discoverable devices
                        // but they may not have an active session/key.
                        var deviceId = r.Session?.Key.DeviceId ?? r.Name;
                        var deviceType = r.Session?.Key.DeviceType ?? "fanuc-karel";

                        long? lastDataMs = r.LastTelemetryAt == default
                            ? (long?)null
                            : r.LastTelemetryAt.ToUnixTimeMilliseconds();

                        return new IotRosterPublisher.DeviceRosterEntry(
                            deviceId: deviceId,
                            deviceType: deviceType,
                            displayName: r.Name,
                            status: r.Status.ToString(),
                            connectionType: r.ConnectionType.ToString(),
                            lastDataMs: lastDataMs
                        );
                    }).ToList()
                );

            List<IotRosterPublisher.DeviceRosterEntry> plcSnapshot;
            lock (_plcLastFrameAt)
            {
                plcSnapshot = _plcSessions.Select(plc =>
                {
                    _plcLastFrameAt.TryGetValue(plc.Key, out var ts);
                    long? lastDataMs = ts == default ? (long?)null : ts.ToUnixTimeMilliseconds();

                    return new IotRosterPublisher.DeviceRosterEntry(
                        deviceId: plc.Key.DeviceId,
                        deviceType: plc.Key.DeviceType,          // "plc-ab"
                        displayName: plc.Key.DeviceId,
                        status: plc.Status.ToString(),
                        connectionType: "plc",
                        lastDataMs: lastDataMs
                    );
                }).ToList();
            }

            var all = robotsSnapshot.Concat(plcSnapshot).ToList();
            return _rosterPublisher.PublishDevicesAsync(all, CancellationToken.None);
        }

        private static X509Certificate2 LoadClientCertFromPfx(string pfxPath, string? password)
        {
            var cert = new X509Certificate2(
                pfxPath,
                password,
                X509KeyStorageFlags.MachineKeySet |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.Exportable);

            if (!cert.HasPrivateKey)
                throw new InvalidOperationException($"Certificate loaded from '{pfxPath}' has no private key.");

            return cert;
        }

        // -------------------------------
        // Robot connect/disconnect (UI unchanged)
        // -------------------------------
        private async void ConnectSelected()
        {
            var vm = SelectedRobot;
            if (vm == null) return;

            vm.SetStatus(RobotStatus.Connecting);
            DisconnectCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();

            // Always create a fresh session on connect
            if (vm.Session != null)
            {
                try { await vm.Session.DisconnectAsync(); } catch { }
                vm.Session = null;
            }

            // Ensure the model name matches the VM name at connect time
            // so session.Key.DeviceId is stable and matches roster + ingress routing.
            vm.Model.Name = vm.Name;

            vm.Session = new RobotSession(vm.Model, () =>
            {
                return vm.ConnectionType switch
                {
                    ConnectionType.KarelSocket => new KarelTransport(),
                    ConnectionType.Pcdk => new PcdkTransport(),
                    _ => throw new ArgumentOutOfRangeException()
                };
            });

            var session = vm.Session;

            // Freeze identity at connect time
            var key = session.Key;

            // Register for ingress routing (FULL key)
            _targets[key] = session;

            ApplyUserAPlanCommand.RaiseCanExecuteChanged();
            ApplyUserBPlanCommand.RaiseCanExecuteChanged();
            RemoveUserBCommand.RaiseCanExecuteChanged();

            if (_iotEgress == null)
                System.Diagnostics.Debug.WriteLine("[IoT-E] Not started; telemetry will not publish to AWS IoT.");

            // Publish gating based on active users. allowed=false clears cached latest (egress invariant).
            session.ActiveUsersChanged += hasAny =>
            {
                try { _iotEgress?.SetPublishAllowed(key, hasAny); } catch { }
            };

            // Always enqueue frames; egress ignores them unless publish-allowed.
            session.TelemetryUpdated += frame =>
            {
                try { _iotEgress?.Enqueue(key, frame); } catch { }

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (frame.JointsDeg is not null)
                        vm.SetJoints(frame.JointsDeg);

                    vm.LastDI = frame.DI;
                    vm.LastGI = frame.GI;
                    vm.LastGO = frame.GO;
                    vm.LastTelemetryAt = frame.Timestamp;
                });
            };

            session.RobotStatusChanged += (status, error) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    vm.SetStatus(status, error);

                    ConnectCommand.RaiseCanExecuteChanged();
                    DisconnectCommand.RaiseCanExecuteChanged();
                    ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                    ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                    RemoveUserBCommand.RaiseCanExecuteChanged();
                });

                // Hardening: any disconnect/error disables publish + clears cache
                if (status == RobotStatus.Disconnected || status == RobotStatus.Error)
                {
                    try { _iotEgress?.SetPublishAllowed(key, false); } catch { }
                }

                // When robot session ends, remove it from ingress routing registry
                if (status == RobotStatus.Disconnected || status == RobotStatus.Error)
                {
                    _targets.Remove(key);
                }

                _ = PublishDevicesRosterAsync();
            };

            try
            {
                await session.ConnectAsync();
            }
            catch (Exception ex)
            {
                vm.SetStatus(RobotStatus.Error, ex.Message);
                try { _iotEgress?.SetPublishAllowed(key, false); } catch { }
                _targets.Remove(key);
            }
            finally
            {
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                RemoveUserBCommand.RaiseCanExecuteChanged();

                RebuildTargetsRegistry();
                _ = PublishDevicesRosterAsync();
            }
        }

        private async void DisconnectSelected()
        {
            var vm = SelectedRobot;
            var session = vm?.Session;
            if (vm == null || session == null) return;

            var key = session.Key;

            try
            {
                // Must stop cloud publishing + drop cached frame regardless of streaming state
                try { _iotEgress?.SetPublishAllowed(key, false); } catch { }

                try { await session.StopStreamingAsync(); } catch { } // optional
                await session.DisconnectAsync();

                vm.Session = null;
            }
            catch
            {
                vm.SetStatus(RobotStatus.Disconnected, null);
            }
            finally
            {
                _targets.Remove(key);

                ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                RemoveUserBCommand.RaiseCanExecuteChanged();
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();

                RebuildTargetsRegistry();
                _ = PublishDevicesRosterAsync();
            }
        }

        // -------------------------------
        // Robot list mgmt (UI unchanged)
        // -------------------------------
        private void AddRobot()
        {
            var model = new RobotConfig
            {
                Name = MakeUniqueName("Robot"),
                ConnectionType = ConnectionType.KarelSocket,
                IpAddress = "127.0.0.1",
                KarelPort = 59002,
                PcdkTimeoutMs = 3000
            };

            var vm = new RobotConfigViewModel(model);
            Robots.Add(vm);
            SelectedRobot = vm;

            _ = PublishDevicesRosterAsync();
        }

        private void RemoveSelectedRobot()
        {
            if (SelectedRobot == null) return;

            var session = SelectedRobot.Session;
            if (session != null)
            {
                try { _iotEgress?.SetPublishAllowed(session.Key, false); } catch { }
                _targets.Remove(session.Key);
            }

            Robots.Remove(SelectedRobot);
            SelectedRobot = Robots.FirstOrDefault();

            RebuildTargetsRegistry();
            _ = PublishDevicesRosterAsync();
        }

        private void Save() => _robotStore.Save(Robots.Select(r => r.Model));

        private void Reload()
        {
            Robots.Clear();
            foreach (var r in _robotStore.Load())
                Robots.Add(new RobotConfigViewModel(r));

            SelectedRobot = Robots.FirstOrDefault();

            // Reload PLC configs too (still headless; sessions are started in StartIotAsync)
            LoadPlcConfigs();

            RebuildTargetsRegistry();
            _ = PublishDevicesRosterAsync();
        }

        private string MakeUniqueName(string baseName)
        {
            var i = 1;
            var name = $"{baseName}{i}";
            while (Robots.Any(r => r.Name == name))
            {
                i++;
                name = $"{baseName}{i}";
            }
            return name;
        }

        // -------------------------------
        // Debug plan buttons (robot-only)
        // -------------------------------
        private void ApplyUserAPlan()
        {
            var s = SelectedRobot?.Session;
            if (s == null) return;

            s.SetUserPlan("userA", new TelemetryPlan(
                DI: new[] { 105 },
                GI: new[] { 1 },
                GO: new[] { 1 }
            ));
        }

        private void ApplyUserBPlan()
        {
            var s = SelectedRobot?.Session;
            if (s == null) return;

            s.SetUserPlan("userB", new TelemetryPlan(
                DI: new[] { 113, 105 },
                GI: new[] { 2 },
                GO: Array.Empty<int>()
            ));
        }

        private void RemoveUserB()
        {
            var s = SelectedRobot?.Session;
            if (s == null) return;

            s.RemoveUser("userB");
        }
    }
}
