namespace TwinSync_Gateway.Services;

public interface IDeviceFrame
{
    DateTimeOffset Timestamp { get; }
    long Sequence { get; }
}
