using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Models;


namespace RIoT2.Net.Devices.Catalog
{
    internal class NetatmoSecurity : NetatmoBase, IDeviceWithConfiguration, IRefreshableReportDevice, ICommandDevice
    {
        private NetatmoHomesData _home;
        private NetatmoSecurityEvents _securityEvents;
        private IDownloadService _downloadService;
        private IMemoryStorageService _memoryStorageService;
        private ILogger _logger;

        internal NetatmoSecurity(ILogger logger, IDownloadService downloadService, IMemoryStorageService memoryStorageService) : base(logger)
        {
            _logger = logger;
            _securityEvents = new NetatmoSecurityEvents();
            _downloadService = downloadService;
            _memoryStorageService = memoryStorageService;
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Netatmo security device";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>
            {
                { "token", "123ecasc" },
                { "refresh_token", "mnbbcc123" },
                { "clientId", "clientid" },
                { "clientSecret", "adfdfd-dfdf-df" }
            };
            deviceConfiguration.ClassFullName = this.GetType().FullName;
            deviceConfiguration.RefreshSchedule = "0 0/5 0 ? * * *";

            var reportConfigurations = new List<ReportTemplate>();
            var data = GetNetatmoHomeStatus(_home.body.homes[0].id).Result;
            if (data == null)
                return deviceConfiguration;

            foreach (var module in data.body.home.modules)
            {
                if (module.type == "NACamera")
                {
                    reportConfigurations.Add(new ReportTemplate()
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = module.id,
                        Name = "Indoor camera",
                        Type = Core.ValueType.Entity,
                        Filters = new List<string>() 
                        {
                            "image", "no-image"
                        }
                    });
                }

                if (module.type == "NOC")
                {
                    reportConfigurations.Add(new ReportTemplate()
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = module.id,
                        Name = "Outdoor camera",
                        Type = Core.ValueType.Entity,
                        Filters = new List<string>()
                        {
                            "image", "no-image"
                        }
                    });
                }
            }

            deviceConfiguration.ReportTemplates = reportConfigurations;
            return deviceConfiguration;
        }

        public override void Refresh(ReportTemplate report)
        {
            if (report != null) //report should be null because in this case, refresh is defined on device level
                return;

            generateSecurityReports().Wait();
        }

        public override void ConfigureDevice()
        {
            string token = GetConfiguration<string>("token");
            string refreshToken = GetConfiguration<string>("refresh_token");
            string clientId = GetConfiguration<string>("clientId");
            string clientSecret = GetConfiguration<string>("clientSecret");

            ConfigureNetatmo(token, refreshToken, clientId, clientSecret);
        }

        public void Start()
        {
            _home = GetNetatmoHomesData().Result;

            if (_home == null || _home.status.ToLower() != "ok") 
            {
                _logger.LogWarning("Could not start NetatmoSecurity device.");
                return;
            }

            generateSecurityReports().Wait();
        }
        public void Stop()
        {
            //no actions needed
        }

        private async Task generateSecurityReports()
        {
            if (_home == null)
                return;

            var data = await GetNetatmoHomeStatus(_home.body.homes[0].id);
            var events = await GetNetatmoEvents(_home.body.homes[0].id);
            if (data == null || events == null || data?.status.ToLower() != "ok")
                return;

            var newEvents = _securityEvents.Update(events?.body?.home?.events);

            foreach (var e in newEvents)
            {
                List<Report> reports = new List<Report>();

                if (e.subevents != null)
                {
                    foreach (var se in e.subevents)
                    {
                        var template = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == $"{e.module_id}");
                        if (template == null)
                            continue;

                        reports.Add(new Report()
                        {
                            Id = template.Id,
                            Value = new ValueModel(new SecurityReport(se)),
                            TimeStamp = DateTime.UtcNow.ToEpoch(),
                            Filter = se.snapshot != null ? "image" : "no-image"
                        });
                    }
                }
                else
                {
                    var template = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == $"{e.module_id}");
                    if (template == null)
                        continue;

                    reports.Add(new Report()
                    {
                        Id = template.Id,
                        Value = new ValueModel(new SecurityReport(e, _home.body.homes[0].persons)),
                        TimeStamp = DateTime.UtcNow.ToEpoch(),
                        Filter = e.snapshot != null ? "image" : "no-image"
                    });

                }

                if (reports.Count == 0)
                    return;

                //Load file for each report
                foreach (var r in reports) 
                {
                    var securityReport = r.Value.GetAsObject() as SecurityReport;
                    if (securityReport == null)
                        continue;

                    var response = await Core.Utils.Web.GetAsync(securityReport.ImageUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"Could not read file from Netatmo. Status: {response.StatusCode} Content: {await response.Content.ReadAsStringAsync()}");
                        continue;
                    }

                    var content = await response.Content.ReadAsByteArrayAsync();
                    var fileGuid = Guid.NewGuid().ToString();

                    //prepare document
                    Document d = new Document()
                    {
                        Data = content,
                        Epochmt = DateTime.UtcNow.ToEpoch(),
                        Filename = fileGuid,
                        Isfolder = FileOrFolder.File,
                        Filetype = DocumentType.Photo,
                        Filesize = content.Length.ToString(),
                        Properties = new Dictionary<string, string>()
                    };
           
                    //Save file to template store
                    _memoryStorageService.Save(d, r.Id);

                    //update image url
                    securityReport.ImageUrl = _downloadService.GetDownloadUrl(fileGuid);

                    //update value model
                    r.Value = new ValueModel(securityReport);
                }

                //send reports
                foreach (var r in reports) 
                {
                    SendReport(this, r);
                }
            }
        }

        public void ExecuteCommand(string commandId, string value)
        {
            //ínstead of timer, device can be refreshed by command -> webhook
            var command = CommandTemplates.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address == "refresh")
            {
                var task = generateSecurityReports();
                return;
            }
        }
    }
}
