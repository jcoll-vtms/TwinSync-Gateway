using System;

namespace TwinSync_Gateway.Services
{
    public readonly record struct DeviceKey(
        string TenantId,
        string GatewayId,
        string DeviceId,
        string DeviceType
    )
    {
        public override string ToString()
            => $"{TenantId}/{GatewayId}/{DeviceType}/{DeviceId}";
    }
}
