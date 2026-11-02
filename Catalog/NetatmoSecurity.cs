using RIoT2.Core.Plugins;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;


namespace RIoT2.Net.Devices.Catalog
{
    [RequiresHardware("Netatmo API")]
    public class NetatmoSecurity : NetatmoBase, IDeviceWithConfiguration, IAsyncCommandDevice
    {
        private NetatmoHomesData _home;
        private NetatmoSecurityEvents _securityEvents;
        private IMemoryStorageService _memoryStorageService;
        private ILogger _logger;

        public NetatmoSecurity(ILogger logger, IMemoryStorageService memoryStorageService, INetatmoAuthClientFactory authClients) : base(logger, authClients)
        {
            _logger = logger;
            _securityEvents = new NetatmoSecurityEvents();
            _memoryStorageService = memoryStorageService;
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Netatmo Security";
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
            var commandConfigurations = new List<CommandTemplate>();

            if (State != DeviceState.Running)
                return deviceConfiguration;

            //var data = GetNetatmoHomeStatus(_home.body.homes[0].id).Result;
            if (_home == null)
                return deviceConfiguration;

            foreach (var module in _home.body.homes[0].modules)
            {
                if(module.type.ToLower() != "noc" && module.type.ToLower() != "nacamera")
                    continue;

                reportConfigurations.Add(new ReportTemplate()
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = module.id,
                    Name = $"{module.name} ({module.type})",
                    Type = Core.ValueType.Entity,
                    Model = new ValueModel(new SecurityReport() {
                        ImageUrl = "http://url-to-image",
                        SecurityEvent = SecurityEventType.motionDetected,
                        Message = "msg",
                        Source = "source",
                        EventValue = ""
                    }),
                    Filters = new List<string>() 
                    {
                        "image", "no-image"
                    }
                });
            }

            //Add commands
            commandConfigurations.Add(new CommandTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh",
                Address = "refresh",
                Type = Core.ValueType.Boolean,
                Model = true
            });

            commandConfigurations.Add(new CommandTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Set Home",
                Address = "set_home",
                Type = Core.ValueType.Boolean,
                Model = true
            });

            deviceConfiguration.CommandTemplates = commandConfigurations;
            deviceConfiguration.ReportTemplates = reportConfigurations;
            return deviceConfiguration;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : generateSecurityReports(cancellationToken);

        public override void ConfigureDevice()
        {
            string token = GetConfiguration<string>("token");
            string refreshToken = GetConfiguration<string>("refresh_token");
            string clientId = GetConfiguration<string>("clientId");
            string clientSecret = GetConfiguration<string>("clientSecret");

            ConfigureNetatmo(token, refreshToken, clientId, clientSecret);
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _home = await GetNetatmoHomesData(cancellationToken).ConfigureAwait(false);

            if (_home == null || _home.status.ToLower() != "ok") 
            {
                _logger.LogWarning("Could not start NetatmoSecurity device.");
                return;
            }

            await generateSecurityReports(cancellationToken).ConfigureAwait(false);
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task generateSecurityReports(CancellationToken cancellationToken)
        {
            if (_home == null || ReportTemplates == null || ReportTemplates?.Count() == 0)
                return;

            var data = await GetNetatmoHomeStatus(_home.body.homes[0].id, cancellationToken).ConfigureAwait(false);
            var events = await GetNetatmoEvents(_home.body.homes[0].id, cancellationToken).ConfigureAwait(false);
            if (data == null || events == null || data?.status.ToLower() != "ok")
                return;

            var newEvents = _securityEvents.Update(events?.body?.home?.events);

            foreach (var e in newEvents)
            {
                List<Report> reports = [];

                if (e.subevents != null)
                {
                    foreach (var se in e.subevents)
                    {
                        var template = ReportTemplates.FirstOrDefault(x => x.Address.Equals(e.module_id, StringComparison.CurrentCultureIgnoreCase));
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
                    var template = ReportTemplates.FirstOrDefault(x => x.Address.Equals(e.module_id, StringComparison.CurrentCultureIgnoreCase));
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
                    continue;

                //Load file for each report
                foreach (var r in reports) 
                {
                    var securityReport = r.Value.GetAsObject() as SecurityReport;
                    if (securityReport == null)
                        continue;

                    if(String.IsNullOrEmpty(securityReport.ImageUrl))
                        continue;

                    using var request = new HttpRequestMessage(HttpMethod.Get, securityReport.ImageUrl);
                    HttpResponseMessage sent;
                    try
                    {
                        sent = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                    {
                        // An HTTP timeout, not a cancellation: the device is in error, not stopped.
                        throw new TimeoutException("Netatmo did not send the snapshot in time.", error);
                    }
                    using var response = sent;
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"Could not read file from Netatmo. Status: {response.StatusCode} Content: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
                        continue;
                    }

                    var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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

                    //update image url
                    securityReport.ImageUrl = _memoryStorageService.Save(d, r.Id);

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

        public void ExecuteCommand(string commandId, string value) =>
            LegacySync.Wait(ExecuteCommandAsync(commandId, value, CancellationToken.None));

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            //ínstead of timer, device can be refreshed by command -> webhook
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address == "refresh")
            {
                await generateSecurityReports(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (command.Address != "set_home")
                return;

            if (_home?.body?.homes == null || _home.body.homes.Count == 0)
                throw new DeviceNotReadyException("The Netatmo home isn't loaded yet.");

            var home = _home.body.homes[0];
            bool toState = value.Equals("true", StringComparison.CurrentCultureIgnoreCase);
            var response = toState
                ? await SetPersonsHome(home.id, [home.persons[0].id], cancellationToken).ConfigureAwait(false)
                : await SetPersonsAway(home.id, cancellationToken).ConfigureAwait(false);
            if (response?.status?.ToLower() != "ok")
                _logger.LogWarning(toState ? "Could not set person home." : "Could not set person away.");
        }

    }
}
