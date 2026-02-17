using System;
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
        // Gateway identity (until you move this into config)
        private const string TenantId = "default";
        private const string GatewayId = "TwinSyncGateway-001";

        private IotMqttConnection? _mqtt;
        private IotPlanIngress? _iotIngress;
        private IotDataEgress? _iotEgress;
        private IotRosterPublisher? _rosterPublisher;

        private readonly RobotConfigStore _store = new RobotConfigStore();

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

        // Temp for debugging
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

        public async Task StartIotAsync()
        {
            if (_mqtt != null) return;

            var endpointHost = "a2oo54s3mt6et0-ats.iot.us-east-1.amazonaws.com";
            var mqttClientId = GatewayId;

            var cert = LoadClientCertFromPfx(
                pfxPath: @"C:\Users\jcollison\source\TwinSync_cert\TwinSyncGateway-001.pfx",
                password: "321cba"
            );

            _mqtt = new IotMqttConnection();
            _mqtt.Log += msg => System.Diagnostics.Debug.WriteLine($"[MQTT] {msg}");

            await _mqtt.ConnectAsync(endpointHost, 8883, mqttClientId, cert, CancellationToken.None);

            _rosterPublisher = new IotRosterPublisher(_mqtt, TenantId, GatewayId);

            // ✅ Multi-device ingress routing:
            // Given a DeviceKey (tenant/gateway/type/id), return the active session/target.
            _iotIngress = new IotPlanIngress(
                mqtt: _mqtt,
                tenantId: TenantId,
                gatewayId: GatewayId,
                getTargetByKey: key =>
                {
                    // For now: only robots exist. We'll add PLC sessions later.
                    // Match by device id == robot name.
                    var vm = Robots.FirstOrDefault(r => r.Session != null && r.Session.Key.DeviceId == key.DeviceId);
                    return vm?.Session as IPlanTarget;
                }
            );

            _iotIngress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT] {msg}");
            await _iotIngress.SubscribeAsync(CancellationToken.None);

            _iotEgress = new IotDataEgress(_mqtt, TenantId, GatewayId);
            _iotEgress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT-E] {msg}");
            _iotEgress.Start(TimeSpan.FromMilliseconds(30)); // publish tick

            await PublishRosterAsync();
        }

        public async Task StopIotAsync()
        {
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

        private Task PublishRosterAsync()
        {
            if (_rosterPublisher == null) return Task.CompletedTask;

            (string Name, string Status, string ConnectionType, long? LastTelemetryMs)[] snapshot =
                Application.Current.Dispatcher.Invoke(() =>
                    Robots.Select(r =>
                    {
                        long? lastTelemetryMs = r.LastTelemetryAt == default
                            ? (long?)null
                            : r.LastTelemetryAt.ToUnixTimeMilliseconds();

                        return (
                            Name: r.Name,
                            Status: r.Status.ToString(),
                            ConnectionType: r.ConnectionType.ToString(),
                            LastTelemetryMs: lastTelemetryMs
                        );
                    }).ToArray()
                );

            return _rosterPublisher.PublishAsync(snapshot, CancellationToken.None);
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

            // IMPORTANT: freeze the device identity at connect time.
            // If the user renames later, we still publish under the connected identity.
            var key = session.Key;

            ApplyUserAPlanCommand.RaiseCanExecuteChanged();
            ApplyUserBPlanCommand.RaiseCanExecuteChanged();
            RemoveUserBCommand.RaiseCanExecuteChanged();

            if (_iotEgress == null)
                System.Diagnostics.Debug.WriteLine("[IoT-E] Not started; telemetry will not publish to AWS IoT.");

            // ✅ Publish gating based on active users. allowed=false clears cached latest (egress invariant).
            session.ActiveUsersChanged += hasAny =>
            {
                try { _iotEgress?.SetPublishAllowed(key, hasAny); } catch { }
            };

            // ✅ Always enqueue frames; egress ignores them unless publish-allowed.
            session.TelemetryUpdated += frame =>
            {
                try { _iotEgress?.Enqueue(key, frame); } catch { }

                System.Diagnostics.Debug.WriteLine(
                    $"Frame: J={(frame.JointsDeg != null)} DI={(frame.DI?.Count ?? 0)} GI={(frame.GI?.Count ?? 0)} GO={(frame.GO?.Count ?? 0)}");

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

                // ✅ Hardening: any disconnect/error disables publish + clears cache.
                if (status == RobotStatus.Disconnected || status == RobotStatus.Error)
                {
                    try { _iotEgress?.SetPublishAllowed(key, false); } catch { }
                }

                _ = PublishRosterAsync();
            };

            try
            {
                await session.ConnectAsync();
            }
            catch (Exception ex)
            {
                vm.SetStatus(RobotStatus.Error, ex.Message);
                try { _iotEgress?.SetPublishAllowed(key, false); } catch { }
            }
            finally
            {
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                RemoveUserBCommand.RaiseCanExecuteChanged();
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
                // ✅ Must stop cloud publishing + drop cached frame regardless of streaming state
                try { _iotEgress?.SetPublishAllowed(key, false); } catch { }

                // Disconnect session
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
                ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                RemoveUserBCommand.RaiseCanExecuteChanged();
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
        }

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

            _ = PublishRosterAsync();
        }

        private void RemoveSelectedRobot()
        {
            if (SelectedRobot == null) return;

            var session = SelectedRobot.Session;
            if (session != null)
            {
                try { _iotEgress?.SetPublishAllowed(session.Key, false); } catch { }
            }

            Robots.Remove(SelectedRobot);
            SelectedRobot = Robots.FirstOrDefault();

            _ = PublishRosterAsync();
        }

        private void Save() => _store.Save(Robots.Select(r => r.Model));

        private void Reload()
        {
            Robots.Clear();
            foreach (var r in _store.Load())
                Robots.Add(new RobotConfigViewModel(r));

            SelectedRobot = Robots.FirstOrDefault();
            _ = PublishRosterAsync();
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
