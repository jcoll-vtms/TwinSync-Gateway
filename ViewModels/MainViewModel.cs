using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Xml.Linq;
using TwinSync_Gateway.Models;
using TwinSync_Gateway.Services;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace TwinSync_Gateway.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private IotMqttConnection? _mqtt;
        private IotPlanIngress? _iotIngress;
        private IotTelemetryEgress? _iotEgress;
        private IotRosterPublisher? _rosterPublisher;

        private readonly RobotConfigStore _store = new RobotConfigStore();

        private bool CanPlanSelected()
    => SelectedRobot?.Session != null && SelectedRobot.Status != RobotStatus.Disconnected;

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


        //Temp for debugging
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

        public async Task StartIotAsync()
        {
            if (_mqtt != null) return;

            var tenantId = "default";
            var gatewayId = "TwinSyncGateway-001";

            var endpointHost = "a2oo54s3mt6et0-ats.iot.us-east-1.amazonaws.com";
            var mqttClientId = gatewayId;

            var cert = LoadClientCertFromPfx(
                pfxPath: @"C:\Users\jcollison\source\TwinSync_cert\TwinSyncGateway-001.pfx",
                password: "321cba"
            );

            _mqtt = new IotMqttConnection();
            _mqtt.Log += msg => System.Diagnostics.Debug.WriteLine($"[MQTT] {msg}");

            await _mqtt.ConnectAsync(endpointHost, 8883, mqttClientId, cert, CancellationToken.None);

            _rosterPublisher = new IotRosterPublisher(_mqtt, tenantId, gatewayId);

            _iotIngress = new IotPlanIngress(
                mqtt: _mqtt,
                tenantId: tenantId,
                gatewayId: gatewayId,
                getSessionByRobot: robotName =>
                    Robots.FirstOrDefault(r => r.Name == robotName)?.Session
            );
            _iotIngress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT] {msg}");

            await _iotIngress.SubscribeAsync(CancellationToken.None);

            _iotEgress = new IotTelemetryEgress(_mqtt, tenantId, gatewayId);
            _iotEgress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT-E] {msg}");
            _iotEgress.Start(TimeSpan.FromMilliseconds(30)); // Publish Hz

            await PublishRosterAsync();
        }

        public async Task StopIotAsync()
        {
            // Stop publisher loop first (so no one tries to publish while disconnecting)
            if (_iotEgress != null)
            {
                _iotEgress?.ClearAll();
                try { await _iotEgress.DisposeAsync(); } catch { }
                _iotEgress = null;
            }

            // Ingress is just a router; nothing to dispose (unless you add disposal later)
            _iotIngress = null;

            // Disconnect the shared MQTT connection last
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


        private bool CanConnectSelected()
            => SelectedRobot != null && SelectedRobot.Status == RobotStatus.Disconnected;

        private bool CanDisconnectSelected()
            => SelectedRobot != null && SelectedRobot.Status != RobotStatus.Disconnected;

        private async void ConnectSelected()
        {
            var vm = SelectedRobot;
            if (vm == null) return;

            vm.SetStatus(RobotStatus.Connecting);
            DisconnectCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();


            // Always create a fresh session on connect (simplest + most reliable)
            if (vm.Session != null)
            {
                try { await vm.Session.DisconnectAsync(); } catch { }
                vm.Session = null;
            }

            vm.Session = new RobotSession(vm.Model, () =>
            {
                return vm.ConnectionType switch
                {
                    ConnectionType.KarelSocket => new KarelTransport(),
                    ConnectionType.Pcdk => new PcdkTransport(),
                    _ => throw new ArgumentOutOfRangeException()
                };
            });

            // IMPORTANT: when the last user drops off (leave / lease timeout),
            // robot streaming stops, so TelemetryUpdated won't fire again to trigger ClearRobot.
            // Clear the AWS egress cache immediately on the transition to "no users".
            vm.Session.ActiveUsersChanged += any =>
            {
                _iotEgress?.SetPublishEnabled(vm.Name, any);
            };

            ApplyUserAPlanCommand.RaiseCanExecuteChanged();
            ApplyUserBPlanCommand.RaiseCanExecuteChanged();
            RemoveUserBCommand.RaiseCanExecuteChanged();


            //can remove this once all frame debugging is complete
            //vm.Session.JointsUpdated += joints =>
            //    {
            //        Application.Current.Dispatcher.Invoke(() =>
            //        {
            //            vm.SetJoints(joints);   
            //        });
            //    };

            vm.Session.TelemetryUpdated += frame =>
            {
                // Only publish to AWS if at least one user is alive for this robot
                if (vm.Session != null && vm.Session.HasAnyActiveUsers())
                    _iotEgress?.Enqueue(vm.Name, frame);

                // Your existing UI/debug work stays the same
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

            vm.Session.StatusChanged += (status, error) =>
            {
                // Marshal back onto UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    vm.SetStatus(status, error);
                    ConnectCommand.RaiseCanExecuteChanged();
                    DisconnectCommand.RaiseCanExecuteChanged();
                    ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                    ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                    RemoveUserBCommand.RaiseCanExecuteChanged();
                });

                _ = PublishRosterAsync();
            };


            try
            {
                await vm.Session.ConnectAsync();
                //await vm.Session.StartStreamingAsync(30); <-redundant if auto-starting in Session
            }
            catch (Exception ex)
            {
                vm.SetStatus(RobotStatus.Error, ex.Message);
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
            if (vm?.Session == null) return;

            try
            {
                // ✅ Stop MQTT publishing immediately + drop cached frame
                _iotEgress?.ClearRobot(vm.Name);         
                await vm.Session.StopStreamingAsync(); // optional if DisconnectAsync already does this
                await vm.Session.DisconnectAsync();
                ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                RemoveUserBCommand.RaiseCanExecuteChanged();
            }
            catch
            {
                vm.SetStatus(RobotStatus.Disconnected, null);
                _iotEgress?.ClearRobot(vm.Name);
            }
            finally
            {
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
        }

        // Existing Add/Remove/Save/Reload unchanged (keep your prior code)
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

            // User A wants: DI 105, GI 1, GO 1
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

            // User B wants: DI 113 + 105, GI 2, GO none
            // (Union should become: DI {105,230}, GI {1,2}, GO {1})
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
