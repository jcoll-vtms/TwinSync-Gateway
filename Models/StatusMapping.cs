using System;
using TwinSync_Gateway.Services;

namespace TwinSync_Gateway.Models
{
    public static class StatusMapping
    {
        public static RobotStatus ToRobotStatus(DeviceStatus s)
        {
            return s switch
            {
                DeviceStatus.Disconnected => RobotStatus.Disconnected,
                DeviceStatus.Connecting => RobotStatus.Connecting,
                DeviceStatus.Connected => RobotStatus.Connected,
                DeviceStatus.Streaming => RobotStatus.Streaming,
                DeviceStatus.Faulted => RobotStatus.Error,
                _ => RobotStatus.Error
            };
        }

        public static string? ToRobotError(Exception? ex)
            => ex?.Message;
    }
}
