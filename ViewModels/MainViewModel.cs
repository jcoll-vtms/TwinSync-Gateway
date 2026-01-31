using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using TwinSync_Gateway.Models;
using TwinSync_Gateway.Services;

namespace TwinSync_Gateway.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
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
            vm.Session.JointsUpdated += joints =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        vm.SetJoints(joints);   
                    });
                };

            vm.Session.TelemetryUpdated += frame =>
            {
                System.Diagnostics.Debug.WriteLine(
    $"Frame: J={(frame.JointsDeg != null)} DI={(frame.DI?.Count ?? 0)} GI={(frame.GI?.Count ?? 0)} GO={(frame.GO?.Count ?? 0)}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Keep joints updates (if you want to move to TelemetryFrame later)
                    if (frame.JointsDeg is not null)
                        vm.SetJoints(frame.JointsDeg);

                    // Optional: stash signal dictionaries on the VM for display/logging
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
