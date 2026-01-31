using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public interface IRobotTransport : IAsyncDisposable
    {
        string Name { get; }
        Task ConnectAsync(RobotConfig config, CancellationToken ct);
    }
}
