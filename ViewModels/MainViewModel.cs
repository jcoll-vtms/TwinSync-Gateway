using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using TwinSync_Gateway.Models;
using TwinSync_Gateway.Services;
using System.Security.Cryptography.X509Certificates;

namespace TwinSync_Gateway.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private IotMqttConnection? _mqtt;
        private IotPlanIngress? _iotIngress;
        private IotTelemetryEgress? _iotEgress;

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

            _iotIngress = new IotPlanIngress(
                mqtt: _mqtt,
                gatewayId: gatewayId,
                getSessionByRobot: robotName =>
                    Robots.FirstOrDefault(r => r.Name == robotName)?.Session
            );
            _iotIngress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT] {msg}");

            await _iotIngress.SubscribeAsync(CancellationToken.None);

            _iotEgress = new IotTelemetryEgress(_mqtt, gatewayId);
            _iotEgress.Log += msg => System.Diagnostics.Debug.WriteLine($"[IoT-E] {msg}");
            _iotEgress.Start(TimeSpan.FromMilliseconds(100)); // 10 Hz
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
                else
                    _iotEgress?.ClearRobot(vm.Name);

                // Your existing UI/debug work stays the same
                System.Diagnostics.Debug.WriteLine(
                    $"Frame: J={(frame.JointsDeg != null)} DI={(frame.DI?.Count ?? 0)} GI={(frame.GI?.Count ?? 0)} GO={(frame.GO?.Count ?? 0)}");

                Application.Current.Dispatcher.Invoke(() =>
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
                await vm.Session.StopStreamingAsync(); // optional if DisconnectAsync already does this
                await vm.Session.DisconnectAsync();
                ApplyUserAPlanCommand.RaiseCanExecuteChanged();
                ApplyUserBPlanCommand.RaiseCanExecuteChanged();
                RemoveUserBCommand.RaiseCanExecuteChanged();
            }
            catch
            {
                vm.SetStatus(RobotStatus.Disconnected, null);
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
        }

        private void RemoveSelectedRobot()
        {
            if (SelectedRobot == null) return;
            Robots.Remove(SelectedRobot);
            SelectedRobot = Robots.FirstOrDefault();
        }

        private void Save() => _store.Save(Robots.Select(r => r.Model));

        private void Reload()
        {
            Robots.Clear();
            foreach (var r in _store.Load())
                Robots.Add(new RobotConfigViewModel(r));

            SelectedRobot = Robots.FirstOrDefault();
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
