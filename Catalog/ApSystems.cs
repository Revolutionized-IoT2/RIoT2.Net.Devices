using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    public class ApSystems : DeviceBase, IRefreshableReportDevice, IDeviceWithConfiguration
    {
        MeterSummary _meterSummary;
        string _ecuId;
        IApSystemsClientService _client;
        public ApSystems(ILogger logger, IApSystemsClientService apSystemsClientService) : base(logger) 
        {
            _client = apSystemsClientService;
        } 

        public override void ConfigureDevice()
        {
            _ecuId = GetConfiguration<string>("ecuId");
            _client.Configure(GetConfiguration<string>("appId"), GetConfiguration<string>("appSecret"), GetConfiguration<string>("sid"));
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "AP Systems Meter";
            deviceConfiguration.RefreshSchedule = "0 0 * ? * * *"; //every hour, every day
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("appId", "j8dl50dk60kg04jd83kdd4f");
            deviceConfiguration.DeviceParameters.Add("appSecret", "985038560184");
            deviceConfiguration.DeviceParameters.Add("sid", "A5609878118987094");
            deviceConfiguration.DeviceParameters.Add("ecuId", "254000099887");

            deviceConfiguration.ClassFullName = this.GetType().FullName;
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

        public override void StartDevice()
        {
           load();
           sendReports();
        }

        public override void StopDevice()
        {
            _meterSummary = null;
        }

        public override void Refresh(ReportTemplate report)
        {
            if (report != null) //report should be null because in this case, refresh is defined on device level
                return;

            load();
            sendReports();
        }

        private List<ReportTemplate> templates() 
        {
            var templates = ReportTemplates?.Where(x => x.Address == "today" || x.Address == "month" || x.Address == "year" || x.Address == "lifetime")?.ToList();
            if (templates == null || templates.Count == 0)
                return [];

            return templates;
        }

        private EnergyMeter getCurrenValue(ReportTemplate template) 
        {
            if (_meterSummary == null)
                return null;

            int precision = -1; //default
            if (template != null && template.Parameters != null && template.Parameters.ContainsKey("precision"))
                _ = int.TryParse(template.Parameters["precision"], out precision);

            return template.Address switch
            {
                "today" => round(_meterSummary.Data.Today, precision),
                "month" => round(_meterSummary.Data.Month, precision),
                "year" => round(_meterSummary.Data.Year, precision),
                "lifetime" => round(_meterSummary.Data.Lifetime, precision),
                _ => null
            };
        }

        private EnergyMeter round(EnergyMeter meter, int precision = -1) 
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

        private void load()
        {
            var summary = _client.GetMeterSummaryAsync(_ecuId).GetAwaiter().GetResult();
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