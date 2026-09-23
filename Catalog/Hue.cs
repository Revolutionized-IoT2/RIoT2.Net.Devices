using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Models;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    public class Hue : DeviceBase, ICommandDevice, IDeviceWithConfiguration, IMatterDevice
    {
        private event HueEventHandler HueEventReceived;
        private string _bridgeIpAddress = "192.168.0.4";
        private string _apikey = "";
        private readonly object _lightStateLock = new();
        private readonly Dictionary<string, HueData> _lightStates = new(StringComparer.OrdinalIgnoreCase);

        CancellationTokenSource _cancellationTokenSource;

        private string getHueUrl() 
        {
            return $"https://{_bridgeIpAddress}/clip/v2";
        }
        private string getHueEventUrl()
        {
            return $"https://{_bridgeIpAddress}/eventstream/clip/v2";
        }

        public Hue(ILogger logger) : base(logger) 
        {
        
        }

        public void ExecuteCommand(string commandId, string value)
        {
            Logger.LogInformation("Executing command: {commandId}", commandId);

            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            setLight(command.Address, value).Wait();
        }

        public override void ConfigureDevice()
        {
            _bridgeIpAddress = GetConfiguration<string>("bridgeIpAddress");
            _apikey = GetConfiguration<string>("apiKey");
            lock (_lightStateLock)
                _lightStates.Clear();
        }

        public override void StartDevice()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            HueEventReceived += Hue_HueEventReceived;
            startEventsListener(_cancellationTokenSource.Token);

            //send initial values
            foreach (var light in getLight().Result?.data) 
                sendReport(light);
        }

        private void sendReport(HueData data) 
        {
            if (string.IsNullOrEmpty(data?.id))
            {
                Logger.LogWarning("Ignoring Hue light update without a light id.");
                return;
            }

            lock (_lightStateLock)
            {
                _lightStates.TryGetValue(data.id, out var previous);
                var snapshot = new HueData
                {
                    id = data.id,
                    on = data.on ?? previous?.on,
                    dimming = data.dimming ?? previous?.dimming,
                    color = data.color?.xy != null ? data.color : previous?.color,
                    color_temperature = data.color_temperature ?? previous?.color_temperature
                };
                _lightStates[data.id] = snapshot;

                var report = ReportTemplates?.FirstOrDefault(x =>
                    string.Equals(x.Address, data.id, StringComparison.OrdinalIgnoreCase));
                if (report == null)
                    return;

                SendReport(this, new Report()
                {
                    Id = report.Id,
                    TimeStamp = DateTime.UtcNow.ToEpoch(),
                    Value = new ValueModel(new HueLightCommand(snapshot)),
                    Filter = "light"
                });
            }
        }

        internal void Hue_HueEventReceived(string eventLine)
        {
            //Logger.LogInformation("Hue event received: {eventLine}", eventLine);

            if (String.IsNullOrEmpty(eventLine) || !eventLine.StartsWith("data: ")) //we're only interested on data rows...
                return;

            eventLine = eventLine.Remove(0, 6); //remove "data: " from the beginning of the line to get the json payload

            var hueEvent = eventLine.ToObj<HueEvent[]>();
            if (hueEvent == null)
                return;

            foreach (var e in hueEvent) 
            {
                if (e?.type != "update" || e.data == null)
                    continue;

                foreach (var light in e.data)
                    if (light?.type == "light")
                        sendReport(light);
            }
        }

        public override void StopDevice()
        {
            HueEventReceived -= Hue_HueEventReceived;
            if(_cancellationTokenSource != null)
                _cancellationTokenSource.Cancel();
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Hue";
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

        /// <summary>
        /// Declares every Hue light as an extended colour light on the RIoT Control Bridge, so the lights
        /// show up in a Matter ecosystem such as Google Home.
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration instance handed in, because
        /// <see cref="GetConfigurationTemplate"/> mints new template ids on every call. The endpoint id is
        /// derived from the Hue bridge's own light id so that it stays the same across restarts and
        /// template re-imports, which is what keeps a light linked to the same Matter endpoint.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.CommandTemplates == null || configuration.ReportTemplates == null)
                yield break;

            foreach (var command in configuration.CommandTemplates)
            {
                var report = configuration.ReportTemplates.FirstOrDefault(x => x.Address == command.Address);
                if (report == null)
                    continue;

                yield return new MatterEndpointTemplate()
                {
                    Id = $"hue:light:{command.Address}",
                    Name = command.Name,
                    DeviceType = MatterDeviceType.ExtendedColorLight,
                    VendorName = "Signify",
                    ProductName = "Hue light",
                    Attributes = getMatterAttributeBindings(report.Id).ToList(),
                    Commands = getMatterCommandBindings(command.Id).ToList()
                };
            }
        }

        //RIoT -> Matter. Reports are sent from sendReport with the "light" filter and a HueLightCommand value.
        private IEnumerable<MatterAttributeBinding> getMatterAttributeBindings(string reportTemplateId)
        {
            yield return new MatterAttributeBinding()
            {
                Attribute = MatterAttribute.OnOff,
                ReportTemplateId = reportTemplateId,
                ValuePath = "state",
                Filter = "light"
            };

            yield return new MatterAttributeBinding()
            {
                Attribute = MatterAttribute.CurrentLevel,
                ReportTemplateId = reportTemplateId,
                ValuePath = "dimming",
                Filter = "light",
                Scale = MatterValueScale.Percent0To100ToLevel0To254
            };

            yield return new MatterAttributeBinding()
            {
                Attribute = MatterAttribute.CurrentHue,
                ReportTemplateId = reportTemplateId,
                ValuePath = "hue",
                Filter = "light",
                Scale = MatterValueScale.Degrees0To360ToHue0To254
            };

            yield return new MatterAttributeBinding()
            {
                Attribute = MatterAttribute.CurrentSaturation,
                ReportTemplateId = reportTemplateId,
                ValuePath = "saturation",
                Filter = "light",
                Scale = MatterValueScale.Percent0To100ToSaturation0To254
            };

            //The Hue bridge reports colour temperature in mirek, which is the Matter unit as well.
            yield return new MatterAttributeBinding()
            {
                Attribute = MatterAttribute.ColorTemperatureMireds,
                ReportTemplateId = reportTemplateId,
                ValuePath = "colorTemperature",
                Filter = "light"
            };
        }

        //Matter -> RIoT. Each binding writes one HueLightCommand property; omitted properties leave the
        //light untouched, so a hue change does not reset brightness.
        private IEnumerable<MatterCommandBinding> getMatterCommandBindings(string commandTemplateId)
        {
            yield return new MatterCommandBinding()
            {
                Attribute = MatterAttribute.OnOff,
                CommandTemplateId = commandTemplateId,
                ValuePath = "state"
            };

            yield return new MatterCommandBinding()
            {
                Attribute = MatterAttribute.CurrentLevel,
                CommandTemplateId = commandTemplateId,
                ValuePath = "dimming",
                Scale = MatterValueScale.Percent0To100ToLevel0To254
            };

            yield return new MatterCommandBinding()
            {
                Attribute = MatterAttribute.CurrentHue,
                CommandTemplateId = commandTemplateId,
                ValuePath = "hue",
                Scale = MatterValueScale.Degrees0To360ToHue0To254
            };

            yield return new MatterCommandBinding()
            {
                Attribute = MatterAttribute.CurrentSaturation,
                CommandTemplateId = commandTemplateId,
                ValuePath = "saturation",
                Scale = MatterValueScale.Percent0To100ToSaturation0To254
            };

            yield return new MatterCommandBinding()
            {
                Attribute = MatterAttribute.ColorTemperatureMireds,
                CommandTemplateId = commandTemplateId,
                ValuePath = "colorTemperature"
            };
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
                                    HueEventReceived?.Invoke(await streamReader.ReadLineAsync());
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
