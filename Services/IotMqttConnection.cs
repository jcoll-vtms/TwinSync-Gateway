using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

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

        private readonly object _rxLock = new();
        private readonly List<Func<MqttApplicationMessage, Task>> _handlers = new();
        private bool _rxHooked;

        public IotMqttConnection()
        {
            _client = new MqttFactory().CreateMqttClient();

            // lifecycle diagnostics
            _client.ConnectedAsync += e =>
            {
                Log?.Invoke("[MQTT] CONNECTED");
                return Task.CompletedTask;
            };

            _client.DisconnectedAsync += e =>
            {
                Log?.Invoke(
                    $"[MQTT] DISCONNECTED reason={e.Reason} " +
                    $"exc={e.Exception?.GetType().Name}: {e.Exception?.Message}"
                );
                return Task.CompletedTask;
            };
        }

        public bool IsConnected => _client.IsConnected;

        /// <summary>
        /// Adds a message handler. Multiple handlers are supported and are invoked sequentially.
        /// </summary>
        public void AddMessageHandler(Func<MqttApplicationMessage, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            bool needHook = false;

            lock (_rxLock)
            {
                _handlers.Add(handler);
                if (!_rxHooked)
                {
                    _rxHooked = true;
                    needHook = true;
                }
            }

            if (!needHook) return;

            // Hook once
            _client.ApplicationMessageReceivedAsync += e =>
            {
                try
                {
                    Log?.Invoke($"[RAW RX] topic='{e.ApplicationMessage.Topic}' bytes={e.ApplicationMessage.PayloadSegment.Count}");

                    Func<MqttApplicationMessage, Task>[] snapshot;
                    lock (_rxLock) snapshot = _handlers.ToArray();

                    return DispatchSequentialAsync(snapshot, e.ApplicationMessage);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"MQTT RX dispatch error: {ex.Message}");
                    return Task.CompletedTask;
                }
            };
        }

        // Back-compat: keep old API name working
        public void SetMessageHandler(Func<MqttApplicationMessage, Task> handler) => AddMessageHandler(handler);

        private async Task DispatchSequentialAsync(Func<MqttApplicationMessage, Task>[] handlers, MqttApplicationMessage msg)
        {
            foreach (var h in handlers)
            {
                try { await h(msg).ConfigureAwait(false); }
                catch (Exception ex) { Log?.Invoke($"MQTT handler error: {ex.Message}"); }
            }
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

            try
            {
                await _client.SubscribeAsync(tf, ct).ConfigureAwait(false);
                Log?.Invoke($"Subscribed: {topicFilter}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Subscribe error for '{topicFilter}': {ex.Message}");
            }
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
            // prevent any future dispatch
            lock (_rxLock)
            {
                _handlers.Clear();
            }

            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch { }

            try
            {
                _client?.Dispose();
            }
            catch { }
        }
    }
}
