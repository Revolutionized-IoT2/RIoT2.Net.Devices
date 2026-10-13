using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Models;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using System.Reflection;
using System.Threading.Channels;

namespace RIoT2.Net.Devices.Services
{
    /// <summary>
    /// The client of the eufy-security-ws WebSocket service.
    /// </summary>
    /// <remarks>
    /// Received messages go through a bounded queue to one worker, which handles them in arrival order;
    /// a message that fails is logged and the next one is still handled. When the queue is full, the
    /// oldest message is dropped and logged.
    /// </remarks>
    public class EufySecurityService : IEufySecurityService, IAsyncDisposable
    {
        /// <summary>Received messages that may wait for the worker before the oldest one is dropped.</summary>
        public const int MessageQueueCapacity = 1000;

        public event EufyEventHandler EufyEvent;

        private readonly WebSocketClient _client;
        private Uri _uri;
        private CancellationTokenSource _shutdownCts;
        private Channel<string> _messages;
        private Task _worker = Task.CompletedTask;
        private ILogger _logger;
        private EufyState _state;
        private EufyPropertiesData _stationProperties;
        private Dictionary<string, EufyPropertiesData> _deviceProperties;

        public EufySecurityService(ILogger<EufySecurityService> logger) 
        {
            _deviceProperties = new Dictionary<string, EufyPropertiesData>();
            _logger = logger;
            _client = new WebSocketClient();

            _client.AutoReconnect.Enabled = true;
            _client.AutoReconnect.InitialDelay = TimeSpan.FromSeconds(1);
            _client.AutoReconnect.MaxDelay = TimeSpan.FromSeconds(15);
            _client.AutoReconnect.MaxAttempts = null; // infinite
            _client.AutoReconnect.JitterFactor = 0.2;
            _client.AutoReconnect.ShouldReconnect = (status, desc, ex) =>
            {
                if (status == System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation) return false;
                return true;
            };

            _client.MessageReceived += client_MessageReceived;
            _client.BinaryMessageReceived += client_BinaryMessageReceived;
            _client.Reconnected += client_Reconnected;
            _client.ListenerFaulted += client_ListenerFaulted;
            _client.Closed += client_Closed;
        }

        public Dictionary<string, EufyPropertiesData> DeviceProperties { get { return _deviceProperties; } }
        public EufyPropertiesData StationProperties { get { return _stationProperties; } }

        public void Configure(string serviceIp, int port)
        {
            _uri = new Uri($"ws://{serviceIp}:{port}");
        }

        private void client_Closed(object sender, EventArgs e)
        {
            _logger.LogWarning("Client Closed.");
        }

        private void client_ListenerFaulted(object sender, Exception e)
        {
            _logger.LogWarning("Client Listener faulted.");
        }

        private void client_Reconnected(object sender, EventArgs e)
        {
            _logger.LogWarning("Client receonnected.");
        }

        private void client_BinaryMessageReceived(object sender, byte[] e)
        {
            _logger.LogWarning("Received binary message from Eufy Security WebSocket service. -> No handler currently");
        }

        private void client_MessageReceived(object sender, string e) => EnqueueMessage(e);

        internal void EnqueueMessage(string message)
        {
            var messages = _messages;
            if (messages == null || !messages.Writer.TryWrite(message))
                _logger.LogDebug("Eufy Security message received while the service is stopped; ignored.");
        }

        private async Task ProcessMessagesAsync(ChannelReader<string> reader)
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var message))
                {
                    try
                    {
                        await HandleMessageAsync(message).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handling Eufy Security WebSocket message; the next messages are still handled.");
                    }
                }
            }
        }

        private async Task HandleMessageAsync(string e)
        {
            var msg = e.ToObj<EufyMessage>();
            if (msg == null)
                return;

            switch (msg.Type)
            {
                case "event":
                    var eventMsg = e.ToObj<EufyEventMessage>();
                    if (eventMsg == null)
                        break;

                    if (eventMsg.Event.Event.ToLower() == "property changed") //We only listen property changed events for now
                    {
                        if (updateProperties(eventMsg))
                            EufyEvent?.Invoke(eventMsg);
                    }
                    break;
                case "version":
                    var versionMsg = e.ToObj<EufyVersionInfo>();
                    //This should be received only once at startup, so we can log it but don't need to raise an event for it
                    await SendCommand(EufyCommand.GetCommand_SetApiSchema("1", versionMsg.MaxSchemaVersion));
                    break;
                case "result":
                    var resultMsg = e.ToObj<EufyResult>();
                    if (!resultMsg.Success)
                    {
                        _logger.LogError("Eufy Security WebSocket command failed: {messageId}", resultMsg.MessageId);
                        break;
                    }

                    if (string.IsNullOrEmpty(msg.MessageId))
                        break;

                    if (msg.MessageId.StartsWith("set_api_schema"))
                    {
                        await SendCommand(EufyCommand.GetCommand_StartListening("2"));
                    }
                    else if (msg.MessageId.StartsWith("start_listening"))
                    {
                        _state = resultMsg.Result.State;
                        await SendCommand(EufyCommand.GetCommand_StationGetProperties("3", _state.Stations[0])); //we only support 1 station for now, so just get properties for the first one

                        if (_state.Devices != null && _state.Devices.Length > 0)
                        {
                            foreach (var device in _state.Devices)
                                await SendCommand(EufyCommand.GetCommand_DeviceGetProperties(device, device));
                        }
                    }
                    else if (msg.MessageId.StartsWith("station.get_properties"))
                    {
                        _stationProperties = resultMsg.Result.Properties;
                        //Get latest info from station, if needed
                        //await SendCommand(EufyCommand.GetCommand_StationDatabaseQueryLatestInfo("4", _state.Stations[0]));
                    }
                    else if (msg.MessageId.StartsWith("device.get_properties"))
                    {
                        if (_deviceProperties.ContainsKey(resultMsg.Result.SerialNumber))
                            _deviceProperties.Remove(resultMsg.Result.SerialNumber);

                        _deviceProperties.Add(resultMsg.Result.SerialNumber, resultMsg.Result.Properties);
                    }
                    break;
                default:
                    _logger.LogWarning("Received unknown message type from Eufy Security WebSocket service: {messageType}", msg.Type);
                    break;
            }
        }

        private bool updateProperties(EufyEventMessage eventMsg)
        {
            if (eventMsg.Event.Source == "station")
            {
                //update station properties
                var propInfo = typeof(EufyPropertiesData).GetProperty(eventMsg.Event.Name, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (propInfo != null)
                {
                    propInfo.SetValue(_stationProperties, Convert.ChangeType(eventMsg.Event.Value, propInfo.PropertyType));
                    return true;
                }
                return false;

            }
            else
            {
                //update device properties
                if (!_deviceProperties.TryGetValue(eventMsg.Event.SerialNumber, out var deviceProps))
                {
                    _logger.LogWarning("Received property changed event for unknown device: {serialNumber}", eventMsg.Event.SerialNumber);
                    return false;
                }

                var propInfo = typeof(EufyPropertiesData).GetProperty(eventMsg.Event.Name, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                if (propInfo != null)
                {
                    //TODO create a more robust way to handle complex properties like the picture, maybe with a custom attribute on the property or something, to avoid hardcoding this in the service
                    if (eventMsg.Event.Name.ToLower() == "picture" && eventMsg.Event.Value != null)
                    {
                        var eufyImage = RIoT2.Core.Utils.Json.Deserialize<EufyImage>(eventMsg.Event.Value.ToString());
                        propInfo.SetValue(deviceProps, eufyImage);
                    }
                    else
                        propInfo.SetValue(deviceProps, Convert.ChangeType(eventMsg.Event.Value, propInfo.PropertyType));

                    return true;
                }
                return false;
            }
        }

        public async Task SendCommand(string json) 
        {
            if(_client.IsConnected)
                await _client.SendAsync(json, _shutdownCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            else
              _logger.LogWarning("Cannot send command, client is not connected.");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_uri == null)
                throw new InvalidOperationException("Configure the Eufy Security service before starting it.");

            // A new run each start: the service is shared and its device may be restarted.
            _shutdownCts?.Dispose();
            _shutdownCts = new CancellationTokenSource();
            var messages = Channel.CreateBounded<string>(new BoundedChannelOptions(MessageQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            }, _ => _logger.LogWarning("Eufy Security message queue full ({Capacity}); dropped the oldest message.", MessageQueueCapacity));
            _messages = messages;
            _worker = Task.Run(() => ProcessMessagesAsync(messages.Reader));

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            try
            {
                await _client.ConnectAsync(_uri, cancellationToken: connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Eufy Security WebSocket connection was stopped.");
            }
            catch (Exception ex)
            {
               _logger.LogError(ex, "Failed to connect to Eufy Security WebSocket service.");
            }
        }

        public async Task StopAsync()
        {
            _shutdownCts?.Cancel();
            try
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disconnecting from the Eufy Security WebSocket service failed.");
            }

            _messages?.Writer.TryComplete();
            await _worker.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            await _client.DisposeAsync().ConfigureAwait(false);
            _shutdownCts?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
