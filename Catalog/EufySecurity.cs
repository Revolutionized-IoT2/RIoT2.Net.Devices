using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

/// This device connects to eufy-security-ws -websocket service
/// Please see https://bropat.github.io/eufy-security-ws/

namespace RIoT2.Net.Devices.Catalog
{
    public class EufySecurity : DeviceBase, IDeviceWithConfiguration, ICommandDevice
    {
        private readonly IEufySecurityService _eufySecurityService;
        private readonly IMemoryStorageService _memoryStorageService;
        private readonly List<string> _supportedEvents = ["motionDetected", "personName", "petDetected", "soundDetected", "strangerPersonDetected", "vehicleDetected"];
        private readonly IDownloadService _downloadService;
        
        public EufySecurity(ILogger logger, IEufySecurityService eufySecurityService, IMemoryStorageService memoryStorageService, IDownloadService downloadService) : base(logger)
        {
            _memoryStorageService = memoryStorageService;
            _eufySecurityService = eufySecurityService;
            _downloadService = downloadService; 
        }

        public override void ConfigureDevice()
        {
            var ip = GetConfiguration<string>("serviceIp");
            var port = GetConfiguration<int>("port");
            _eufySecurityService.Configure(ip, port);
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Eufy Security";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("serviceIp", "192.168.0.36");
            deviceConfiguration.DeviceParameters.Add("port", "3000");
            deviceConfiguration.ClassFullName = this.GetType().FullName;
           
            //Eufy must be connected, other we can't provide templates.
            if (_eufySecurityService.DeviceProperties == null || _eufySecurityService.StationProperties == null) 
                return deviceConfiguration;

            var reportConfigurations = new List<ReportTemplate>();
            var commandConfigurations = new List<CommandTemplate>();

            foreach (var device in _eufySecurityService.DeviceProperties)
            {
                reportConfigurations.Add(new ReportTemplate()
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = device.Key,
                    Name = device.Value.Name,
                    Type = Core.ValueType.Entity,
                    Model = new ValueModel(new SecurityReport()
                    {
                        ImageUrl = "http://image-url",
                        Source = "eufy",
                        EventValue = "event value",
                        SecurityEvent = SecurityEventType.motionDetected
                    })
                });

            }

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = _eufySecurityService.StationProperties.SerialNumber +"|guardMode",
                Name = "Guard Mode", 
                Type = Core.ValueType.Number,
                Model = 1
            });

            //Commands
            commandConfigurations.Add(new CommandTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = _eufySecurityService.StationProperties.SerialNumber + "|guardMode",
                Name = "Set Guard Mode",
                Type = Core.ValueType.Number,
                Model = 1
            });

            //Add device enable/ disable commands
            //Add more commands if needed -> refresh device pictures?

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = commandConfigurations;
            return deviceConfiguration;
        }

        public void ExecuteCommand(string commandId, string value)
        {
            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            //change to more generic, if more commads are added
            if (command.Address == _eufySecurityService.StationProperties.SerialNumber + "|guardMode")
            {
                int mode = 0;
                if(Int32.TryParse(value, out mode))
                    _eufySecurityService.SendCommand(EufyCommand.GetCommand_StationSetProperties(commandId, _eufySecurityService.StationProperties.SerialNumber, "guardMode", mode)).Wait();
            }
        }

        public override void StartDevice()
        {
            Logger.LogInformation("Starting Eufy Security device...");
            _eufySecurityService.Start();
            _eufySecurityService.EufyEvent += _eufySecurityService_EufyEvent;
            //wait a bit to get initial properties, otherwise we won't have templates to send reports with
            Task.Delay(2000).Wait();
            sendInitialReports();

        }

        private void sendInitialReports() 
        {
            foreach (var device in _eufySecurityService.DeviceProperties) //Only send pictures as initial reports.
            {
                sendReport(device.Key, device.Value.Picture, device.Value.Name);
            }

            sendReport(_eufySecurityService.StationProperties.SerialNumber + "|guardMode", _eufySecurityService.StationProperties.GuardMode, "guardMode");
        }

        private void _eufySecurityService_EufyEvent(EufyEventMessage data)
        {
            if (data.Event.Value is bool e && !e)
                return;

            if (data.Event.Source == "station") 
            {
                sendReport(data.Event.SerialNumber + $"|{data.Event.Name}", data.Event.Value, data.Event.Name);
            }
            else
                sendReport(data.Event.SerialNumber, data.Event.Value, data.Event.Name);
        }

        private void sendReport(string address, object propertyValue, string propertyName) 
        {
            var template = ReportTemplates.FirstOrDefault(x => x.Address == address);
            if (template == null) 
                return;

            if (propertyName == "guardMode")
            {
                int modeValue = 0;
                if (Int32.TryParse(propertyValue.ToString(), out modeValue))
                {
                    SendReport(this, new Report()
                    {
                        Id = template.Id,
                        Value = new ValueModel(modeValue),
                        TimeStamp = DateTime.UtcNow.ToEpoch()
                    });
                }
            }
            else 
            {
                string imgUrl = null;
                if (propertyName == "picture") 
                {
                    var fileGuid = Guid.NewGuid().ToString();
                    EufyImage img = propertyValue as EufyImage;
                    Document d = new Document()
                    {
                        Data = img.Data.Data,
                        Epochmt = DateTime.UtcNow.ToEpoch(),
                        Filename = fileGuid,
                        Isfolder = FileOrFolder.File,
                        Filetype = DocumentType.Photo,
                        Filesize = img.Data.Data.Length.ToString(),
                        Properties = new Dictionary<string, string>()
                    };

                    _memoryStorageService.Save(d, template.Id);
                    imgUrl = _downloadService.GetDownloadUrl(fileGuid);
                }

                SendReport(this, new Report()
                {
                    Id = template.Id,
                    Value = new ValueModel(new SecurityReport()
                    {
                        ImageUrl = imgUrl,
                        Source = "eufy",
                        EventValue = propertyName != "picture" ? propertyValue?.ToString() : "true",
                        SecurityEvent = _supportedEvents.Contains(propertyName) ? Enum.Parse<SecurityEventType>(propertyName) : SecurityEventType.motionDetected
                    }),
                    TimeStamp = DateTime.UtcNow.ToEpoch()
                });
            }
        }

        public override void StopDevice()
        {
            _eufySecurityService.Stop();
        }
    }
}
