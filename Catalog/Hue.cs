using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Models;
using System.Diagnostics;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    internal class Hue : DeviceBase, ICommandDevice, IDeviceWithConfiguration
    {
        private event HueEventHandler HueEventReceived;
        private string _bridgeIpAddress = "192.168.0.4";
        private string _apikey = "";

        CancellationTokenSource _cancellationTokenSource;

        private string getHueUrl() 
        {
            return $"https://{_bridgeIpAddress}/clip/v2";
        }
        private string getHueEventUrl()
        {
            return $"https://{_bridgeIpAddress}/eventstream/clip/v2";
        }

        internal Hue(ILogger logger) : base(logger) 
        {
        
        }

        public void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executed command: {commandId}", commandId);

            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            //TODO Mapping to model? or to schema?
            //MAP HUE Model to custom model?

            setLight(command.Address, value).Wait();
        }

        public override void ConfigureDevice()
        {
            _bridgeIpAddress = GetConfiguration<string>("bridgeIpAddress");
            _apikey = GetConfiguration<string>("apiKey");
        }

        public override void StartDevice()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            startEventsListener(_cancellationTokenSource.Token);
            HueEventReceived += Hue_HueEventReceived;

            //send initial values
            foreach (var light in getLight().Result?.data) 
                sendReport(light);
        }

        private void sendReport(HueData data) 
        {
            var report = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == data.id.ToLower());
            if (report == null)
                return;

            SendReport(this, new Report()
            {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(data),
                Filter = "light"
            });
        }

        private void Hue_HueEventReceived(string eventLine)
        {
            if (String.IsNullOrEmpty(eventLine) || !eventLine.StartsWith("data: ")) //we're only interested on data rows...
                return;
            eventLine = eventLine.Remove(0, 7);
            eventLine = eventLine.Remove(eventLine.Length - 1);

            var hueEvent = eventLine.ToObj<HueEvent>();
            if (hueEvent == null || hueEvent.data[0].type != "light" || hueEvent.type != "update") //type = ‘update’, ‘add’, ‘delete’, ‘error’
                return;

            foreach(var light in hueEvent.data)
                sendReport(light);
        }

        public override void StopDevice()
        {
            if(_cancellationTokenSource != null)
                _cancellationTokenSource.Cancel();
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Hue device";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("bridgeIpAddress", "192.168.0.4");
            deviceConfiguration.DeviceParameters.Add("apiKey", "xxxx");
            deviceConfiguration.ClassFullName = this.GetType().FullName;
            deviceConfiguration.RefreshSchedule = null;

            var reportConfigurations = new List<ReportTemplate>();
            var commandConfigurations = new List<CommandTemplate>();

            if(State != DeviceState.Running)
                return deviceConfiguration;

            foreach (var light in getLight().Result.data)
            {
                var id = Guid.NewGuid().ToString();
                commandConfigurations.Add(new CommandTemplate()
                {
                    Id = id,
                    Address = light.id,
                    Name = light.metadata.name,
                    Type = ValueType.Entity,
                    Model = new HueLightCommand(light)
                });

                reportConfigurations.Add(new ReportTemplate()
                {
                    Id = id,
                    Address = light.id,
                    Name = light.metadata.name,
                    Type = ValueType.Entity
                });
            }

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = commandConfigurations;
            return deviceConfiguration;
        }

        private delegate void HueEventHandler(string json);

        private async Task<HueLights> getLight(string hueId = null) 
        {
            string address = getHueUrl() + "/resource/light";
            if (!String.IsNullOrEmpty(hueId))
                address += "/" + hueId;

            var headers = new Dictionary<string, string>();
            headers.Add("hue-application-key", _apikey);

            var response = await RIoT2.Core.Utils.Web.GetAsync(address, headers);
            var json = await response.Content.ReadAsStringAsync();
            return json.ToObj<HueLights>();
        }

        private async Task setLight(string hueId, string commandJson)
        {
            string address = getHueUrl() + $"/resource/light/{hueId}";
            var headers = new Dictionary<string, string>();
            headers.Add("hue-application-key", _apikey);

            var hueCmd = Json.Deserialize<HueLightCommand>(commandJson);
            if(hueCmd != null)
                await RIoT2.Core.Utils.Web.PutAsync(address, Json.SerializeIgnoreNulls(hueCmd.GetCommand()), headers);

            //TODO handle reponse?
        }

        private async void startEventsListener(CancellationToken cancelToken) 
        {
            using (var httpClientHandler = new HttpClientHandler()) 
            {
                httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => { return true; };
                using (var sseClient = new HttpClient(httpClientHandler))
                {
                    sseClient.DefaultRequestHeaders.Add("hue-application-key", _apikey);
                    sseClient.DefaultRequestHeaders.Add("Accept", "text/event-stream");

                    sseClient.Timeout = TimeSpan.FromSeconds(5);
                    while (true)
                    {
                        if (cancelToken.IsCancellationRequested)
                            return;

                        try
                        {
                            using (var streamReader = new StreamReader(await sseClient.GetStreamAsync(getHueEventUrl(), cancelToken)))
                            {
                                while (!streamReader.EndOfStream)
                                {
                                    HueEventReceived(await streamReader.ReadLineAsync());
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Error in hue event listener. Restarting in 5 seconds.");
                            await Task.Delay(TimeSpan.FromSeconds(5));
                        }
                    }
                }
            }
        }
    }
}
