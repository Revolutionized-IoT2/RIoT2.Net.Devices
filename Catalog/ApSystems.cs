using RIoT2.Core.Plugins;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="ApSystems"/> device's parameters: the APsystems OpenAPI credentials and the ECU.</summary>
    public sealed class ApSystemsOptions
    {
        [DeviceParameter("appId", Required = true, Description = "APsystems OpenAPI app id")]
        public string AppId { get; set; } = "";

        [DeviceParameter("appSecret", Required = true, Secret = true, Description = "APsystems OpenAPI app secret")]
        public string AppSecret { get; set; } = "";

        [DeviceParameter("sid", Required = true, Description = "System id")]
        public string Sid { get; set; } = "";

        [DeviceParameter("ecuId", Required = true, Description = "ECU id")]
        public string EcuId { get; set; } = "";
    }

    [RequiresHardware("APsystems API")]
    public class ApSystems : AsyncDeviceBase<ApSystemsOptions>, IDeviceWithConfiguration
    {
        MeterSummary? _meterSummary;
        string? _ecuId;
        IApSystemsClientService _client;
        public ApSystems(ILogger logger, IApSystemsClientService apSystemsClientService) : base(logger) 
        {
            _client = apSystemsClientService;
        } 

        public override void ConfigureDevice()
        {
            _ecuId = Options.EcuId;
            _client.Configure(Options.AppId, Options.AppSecret, Options.Sid);
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "AP Systems Meter";
            deviceConfiguration.RefreshSchedule = "0 0 * ? * * *"; //every hour, every day
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("appId", "<apsystems-app-id>");
            deviceConfiguration.DeviceParameters.Add("appSecret", "<apsystems-app-secret>");
            deviceConfiguration.DeviceParameters.Add("sid", "<apsystems-sid>");
            deviceConfiguration.DeviceParameters.Add("ecuId", "<ecu-id>");

            deviceConfiguration.ClassFullName = this.GetType().FullName!;
            var reportConfigurations = new List<ReportTemplate>();

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "today",
                Name = "Meter Today",
                Type = Core.ValueType.Entity,
                Model = new EnergyMeter()
                {
                    Consumed = "0",
                    Exported = "0",
                    Imported = "0",
                    Produced = "0"
                },
                Parameters = new Dictionary<string, string>() { { "unit", "kWh" }, { "precision", "2" } }
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "month",
                Name = "Meter Month",
                Type = Core.ValueType.Entity,
                Model = new EnergyMeter()
                {
                    Consumed = "0",
                    Exported = "0",
                    Imported = "0",
                    Produced = "0"
                },
                Parameters = new Dictionary<string, string>() { { "unit", "kWh" }, { "precision", "2" } }
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "year",
                Name = "Meter year",
                Type = Core.ValueType.Entity,
                Model = new EnergyMeter()
                {
                    Consumed = "0",
                    Exported = "0",
                    Imported = "0",
                    Produced = "0"
                },
                Parameters = new Dictionary<string, string>() { { "unit", "kWh" }, { "precision", "2" } }
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "lifetime",
                Name = "Meter lifetime",
                Type = Core.ValueType.Entity,
                Model = new EnergyMeter()
                {
                    Consumed = "0",
                    Exported = "0",
                    Imported = "0",
                    Produced = "0"
                },
                Parameters = new Dictionary<string, string>() { { "unit", "kWh" }, { "precision", "2" } }
            });

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = null; //set explicitly to null
            return deviceConfiguration;
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            sendReports();
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _meterSummary = null;
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override async Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken)
        {
            if (report != null)
                return;

            await LoadAsync(cancellationToken).ConfigureAwait(false);
            sendReports();
        }

        private List<ReportTemplate> templates() 
        {
            var templates = ReportTemplates?.Where(x => x.Address == "today" || x.Address == "month" || x.Address == "year" || x.Address == "lifetime")?.ToList();
            if (templates == null || templates.Count == 0)
                return [];

            return templates;
        }

        private EnergyMeter? getCurrenValue(ReportTemplate template) 
        {
            if (_meterSummary == null)
                return null;

            int precision = -1; //default
            if (template != null && template.Parameters != null && template.Parameters.ContainsKey("precision"))
                _ = int.TryParse(template.Parameters["precision"], out precision);

            // The callers pass a template; the null test above is only defensive.
            return template!.Address switch
            {
                "today" => round(_meterSummary.Data.Today, precision),
                "month" => round(_meterSummary.Data.Month, precision),
                "year" => round(_meterSummary.Data.Year, precision),
                "lifetime" => round(_meterSummary.Data.Lifetime, precision),
                _ => null
            };
        }

        private EnergyMeter? round(EnergyMeter? meter, int precision = -1) 
        {
            if (meter == null)
                return null;

            _ = decimal.TryParse(meter.Consumed, out decimal consumed);
            _ = decimal.TryParse(meter.Exported, out decimal exported);
            _ = decimal.TryParse(meter.Imported, out decimal imported);
            _ = decimal.TryParse(meter.Produced, out decimal produced);

            if (precision > -1) 
            {
                meter.Consumed = Math.Round(consumed, precision).ToString();
                meter.Exported = Math.Round(exported, precision).ToString();
                meter.Imported = Math.Round(imported, precision).ToString();
                meter.Produced = Math.Round(produced, precision).ToString();
            }
            return meter;
        }

        private void sendReports() 
        {
            foreach (var template in templates()) 
            {
                SendReportIfValueChanged(this, new Report()
                {
                    Id = template.Id,
                    TimeStamp = DateTime.UtcNow.ToEpoch(),
                    Value = new ValueModel(getCurrenValue(template)),
                    Filter = ""
                });
            }
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            MeterSummary? summary;
            try
            {
                summary = await _client.GetMeterSummaryAsync(_ecuId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                // An HTTP timeout, not a cancellation: the device is in error, not stopped.
                throw new TimeoutException("The APsystems API did not answer in time.", error);
            }
            if (summary != null && summary.Code == ApCode.Success)
            {
                _meterSummary = summary;
            }
            else
            {
                throw new Exception($"Failed to get meter summary with code {summary?.Code}");
            }
        }
    }
}