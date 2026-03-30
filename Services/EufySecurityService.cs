using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Models;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using System.Reflection;

namespace RIoT2.Net.Devices.Services
{
    public class EufySecurityService : IEufySecurityService
    {
        public event EufyEventHandler EufyEvent;

        private WebSocketClient _client;
        private Uri _uri;
        private CancellationTokenSource _shutdownCts;
        private ILogger _logger;
        private EufyState _state;
        private EufyPropertiesData _stationProperties;
        private Dictionary<string, EufyPropertiesData> _deviceProperties;

        public EufySecurityService(ILogger<EufySecurityService> logger) 
        {
            _deviceProperties = new Dictionary<string, EufyPropertiesData>();
            _shutdownCts = new CancellationTokenSource();
            _logger = logger;
        }

        public Dictionary<string, EufyPropertiesData> DeviceProperties { get { return _deviceProperties; } }
        public EufyPropertiesData StationProperties { get { return _stationProperties; } }

        public void Configure(string serviceIp, int port)
        {
            _uri = new Uri($"ws://{serviceIp}:{port}");
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

        private void client_MessageReceived(object sender, string e)
        {
            var msg = e.ToObj<EufyMessage>();

            switch (msg.Type) 
            {
                case "event":
                    var eventMsg = e.ToObj<EufyEventMessage>();
                    if (eventMsg == null)
                        break;

                    if (eventMsg.Event.Event.ToLower() == "property changed") //We only listen property changed events for now
                    {
                        if(updateProperties(eventMsg))
                            EufyEvent?.Invoke(eventMsg);
                    }
                    break;
                case "version":
                    var versionMsg = e.ToObj<EufyVersionInfo>();
                    //This should be received only once at startup, so we can log it but don't need to raise an event for it
                    SendCommand(EufyCommand.GetCommand_SetApiSchema("1", versionMsg.MaxSchemaVersion)).Wait(); 
                    break;
                case "result":
                    var resultMsg = e.ToObj<EufyResult>();
                    if (!resultMsg.Success) 
                    {
                        _logger.LogError("Eufy Security WebSocket command failed: {messageId}", resultMsg.MessageId);
                        break;
                    }
                    
                    if (msg.MessageId.StartsWith("set_api_schema"))
                    {
                        SendCommand(EufyCommand.GetCommand_StartListening("2")).Wait();
                    }
                    else if (msg.MessageId.StartsWith("start_listening"))
                    {
                        _state = resultMsg.Result.State;
                        SendCommand(EufyCommand.GetCommand_StationGetProperties("3", _state.Stations[0])).Wait(); //we only support 1 station for now, so just get properties for the first one

                        if (_state.Devices != null && _state.Devices.Length > 0) 
                        {
                            foreach(var device in _state.Devices)
                                SendCommand(EufyCommand.GetCommand_DeviceGetProperties(device, device)).Wait();
                        }
                    }
                    else if (msg.MessageId.StartsWith("station.get_properties")) 
                    {
                        _stationProperties = resultMsg.Result.Properties;
                        //Get latest info from station, if needed
                        //SendCommand(EufyCommand.GetCommand_StationDatabaseQueryLatestInfo("4", _state.Stations[0])).Wait();
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
                var deviceProps = _deviceProperties[eventMsg.Event.SerialNumber];
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
                await _client.SendAsync(json, _shutdownCts.Token);
            else
              _logger.LogWarning("Cannot send command, client is not connected.");
        }

        public void Start()
        {
            try
            {
                _client.ConnectAsync(_uri, cancellationToken: _shutdownCts.Token).Wait();
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

        public void Stop()
        {
            _shutdownCts.Cancel();
            try
            {
                _client.DisconnectAsync().Wait();
            }
            catch
            { /* ignore */ }
            _ = _client.DisposeAsync();
        }
    }
}
