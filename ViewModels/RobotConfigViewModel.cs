using System;
using System.Collections.Generic;
using System.Linq;
using TwinSync_Gateway.Models;
using TwinSync_Gateway.Services;

namespace TwinSync_Gateway.ViewModels
{
    public sealed class RobotConfigViewModel : ObservableObject
    {
        public RobotConfig Model { get; }

        private RobotStatus _status = RobotStatus.Disconnected;

        private double _j1;
        private double _j2;
        private double _j3;
        private double _j4;
        private double _j5;
        private double _j6;

        public double J1 { get => _j1; set => Set(ref _j1, value); }
        public double J2 { get => _j2; set => Set(ref _j2, value); }
        public double J3 { get => _j3; set => Set(ref _j3, value); }
        public double J4 { get => _j4; set => Set(ref _j4, value); }
        public double J5 { get => _j5; set => Set(ref _j5, value); }
        public double J6 { get => _j6; set => Set(ref _j6, value); }

        public string DIText =>
            LastDI == null || LastDI.Count == 0
                ? "(none)"
                : string.Join(", ", LastDI.Select(kv => $"{kv.Key}:{kv.Value}"));

        public string GIText =>
            LastGI == null || LastGI.Count == 0
                ? "(none)"
                : string.Join(", ", LastGI.Select(kv => $"{kv.Key}:{kv.Value}"));

        public string GOText =>
            LastGO == null || LastGO.Count == 0
                ? "(none)"
                : string.Join(", ", LastGO.Select(kv => $"{kv.Key}:{kv.Value}"));

        public string LastTelemetryText =>
            LastTelemetryAt == default
                ? ""
                : $"Updated {LastTelemetryAt.LocalDateTime:HH:mm:ss.fff}";

        private IReadOnlyDictionary<int, int>? _lastDI;
        public IReadOnlyDictionary<int, int>? LastDI
        {
            get => _lastDI;
            set
            {
                if (Set(ref _lastDI, value))
                    Raise(nameof(DIText));
            }
        }

        private IReadOnlyDictionary<int, int>? _lastGI;
        public IReadOnlyDictionary<int, int>? LastGI
        {
            get => _lastGI;
            set
            {
                if (Set(ref _lastGI, value))
                    Raise(nameof(GIText));
            }
        }

        private IReadOnlyDictionary<int, int>? _lastGO;
        public IReadOnlyDictionary<int, int>? LastGO
        {
            get => _lastGO;
            set
            {
                if (Set(ref _lastGO, value))
                    Raise(nameof(GOText));
            }
        }

        private DateTimeOffset _lastTelemetryAt;
        public DateTimeOffset LastTelemetryAt
        {
            get => _lastTelemetryAt;
            set
            {
                if (Set(ref _lastTelemetryAt, value))
                    Raise(nameof(LastTelemetryText));
            }
        }

        public void SetJoints(double[] joints)
        {
            if (joints is null || joints.Length < 6) return;
            J1 = joints[0];
            J2 = joints[1];
            J3 = joints[2];
            J4 = joints[3];
            J5 = joints[4];
            J6 = joints[5];
        }

        public RobotStatus Status
        {
            get => _status;
            private set => Set(ref _status, value);
        }

        private string? _lastError;
        public string? LastError
        {
            get => _lastError;
            private set => Set(ref _lastError, value);
        }

        // Runtime worker handle (created when connecting)
        // Keep RobotSession for now; later this becomes a device-agnostic interface.
        internal RobotSession? Session { get; set; }

        public RobotConfigViewModel(RobotConfig model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        // Properties bound to UI
        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name != value)
                {
                    Model.Name = value;
                    Raise();
                }
            }
        }

        public ConnectionType ConnectionType
        {
            get => Model.ConnectionType;
            set
            {
                if (Model.ConnectionType != value)
                {
                    Model.ConnectionType = value;
                    Raise();
                }
            }
        }

        public string IpAddress
        {
            get => Model.IpAddress;
            set
            {
                if (Model.IpAddress != value)
                {
                    Model.IpAddress = value;
                    Raise();
                }
            }
        }

        public int KarelPort
        {
            get => Model.KarelPort;
            set
            {
                if (Model.KarelPort != value)
                {
                    Model.KarelPort = value;
                    Raise();
                }
            }
        }

        public string? PcdkRobotName
        {
            get => Model.PcdkRobotName;
            set
            {
                if (Model.PcdkRobotName != value)
                {
                    Model.PcdkRobotName = value;
                    Raise();
                }
            }
        }

        public int PcdkTimeoutMs
        {
            get => Model.PcdkTimeoutMs;
            set
            {
                if (Model.PcdkTimeoutMs != value)
                {
                    Model.PcdkTimeoutMs = value;
                    Raise();
                }
            }
        }

        internal void SetStatus(RobotStatus status, string? error = null)
        {
            Status = status;
            LastError = error;
        }
    }
}
