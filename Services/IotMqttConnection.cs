using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Security.Cryptography.X509Certificates;

namespace TwinSync_Gateway.Services
{
    /// <summary>
    /// Owns the single AWS IoT MQTT connection for the gateway.
    /// Ingress + egress share this one client.
    /// </summary>
    public sealed class IotMqttConnection : IAsyncDisposable
    {
        private readonly IMqttClient _client;

        public event Action<string>? Log;

        public IotMqttConnection()
        {
            _client = new MqttFactory().CreateMqttClient();
        }

        public bool IsConnected => _client.IsConnected;

        public void SetMessageHandler(Func<MqttApplicationMessage, Task> handler)
        {
            // Your MQTTnet build supports ApplicationMessageReceivedAsync
            _client.ApplicationMessageReceivedAsync += e =>
            {
                try
                {
                    return handler(e.ApplicationMessage);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"MQTT message handler error: {ex.Message}");
                    return Task.CompletedTask;
                }
            };
        }

        public async Task ConnectAsync(
            string endpointHost,
            int port,
            string mqttClientId,
            X509Certificate2 clientCert,
            CancellationToken ct)
        {
            var options = new MqttClientOptionsBuilder()
                .WithClientId(mqttClientId)
                .WithTcpServer(endpointHost, port)
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithTls(new MqttClientOptionsBuilderTlsParameters
                {
                    UseTls = true,
                    Certificates = new X509Certificate[] { clientCert },
                    SslProtocol = System.Security.Authentication.SslProtocols.Tls12,

                    AllowUntrustedCertificates = false,
                    IgnoreCertificateChainErrors = false,
                    IgnoreCertificateRevocationErrors = false
                })
                .Build();

            Log?.Invoke($"Cert subject: {clientCert.Subject}");
            Log?.Invoke($"HasPrivateKey: {clientCert.HasPrivateKey}");

            await _client.ConnectAsync(options, ct).ConfigureAwait(false);
            Log?.Invoke("AWS IoT connected.");
        }

        public async Task SubscribeAsync(string topicFilter, MqttQualityOfServiceLevel qos, CancellationToken ct)
        {
            var tf = new MqttTopicFilterBuilder()
                .WithTopic(topicFilter)
                .WithQualityOfServiceLevel(qos)
                .Build();

            await _client.SubscribeAsync(tf, ct).ConfigureAwait(false);
            Log?.Invoke($"Subscribed: {topicFilter}");
        }

        public async Task PublishAsync(string topic, byte[] payload, MqttQualityOfServiceLevel qos, bool retain, CancellationToken ct)
        {
            if (!_client.IsConnected) return;

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();

            await _client.PublishAsync(msg, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch { }
        }
    }
}
