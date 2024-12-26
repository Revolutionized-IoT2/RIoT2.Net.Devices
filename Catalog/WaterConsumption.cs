using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using System.Net.Http.Headers;

namespace RIoT2.Net.Devices.Catalog
{
    internal class WaterConsumption : DeviceBase, IRefreshableReportDevice, IDeviceWithConfiguration
    {
        internal WaterConsumption(ILogger logger) : base(logger) { }

        private string _securityToken = "";
        private string _endpoint = "";
        private List<WaterConsumptionValue> _consumption;

        public override void ConfigureDevice()
        {
            _securityToken = GetConfiguration<string>("securityToken");
            _endpoint = GetConfiguration<string>("endpoint");
        }

        //This is called by base when time trigger is activated
        public override void Refresh(ReportTemplate report)
        {
            if (report != null) //report should be null because in this case, refresh is defined on device level
                return;

            var template = getReportTemplate();
            var latestValue = getLatestValue().Result;

            if (latestValue == null || template == null)
                return;

            latestValue = Math.Round(latestValue.Value, 3);

            SendReport(this, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(latestValue.Value),
                Filter = ""
            });
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Water Consumption Provider";
            deviceConfiguration.RefreshSchedule = "0 * * * *";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("securityToken", Guid.NewGuid().ToString());
            deviceConfiguration.DeviceParameters.Add("endpoint", "https://wmd.wrm-systems.fi/api/watermeter");

            deviceConfiguration.ClassFullName = this.GetType().FullName;
            var reportConfigurations = new List<ReportTemplate>();

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "watermeter",
                Name = "Water consumption",
                Type = Core.ValueType.Number
            });

            deviceConfiguration.ReportTemplates = reportConfigurations;
            return deviceConfiguration;
        }

        public override void StartDevice()
        {
            var template = getReportTemplate();
            var value = getLatestValue().Result;

            if (value == null || template == null)
                return;

            SendReport(this, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(value),
                Filter = ""
            });
        }

        public override void StopDevice()
        {
            _consumption = null;
        }

        private ReportTemplate getReportTemplate()
        {
            return ReportTemplates.FirstOrDefault(x => x.Address == "watermeter");
        }

        private async Task<decimal?> getLatestValue()
        {
            if (_consumption == null)
                _consumption = new List<WaterConsumptionValue>();

            try
            {
                var url = _endpoint + $"?startDate={DateTime.Now.ToString("yyyy-MM-dd")}";
                var auth = new AuthenticationHeaderValue("Bearer", _securityToken);

                var response = await Core.Utils.Web.GetAsync(url, auth);
                var json = await response.Content.ReadAsStringAsync();
                var values = Json.Deserialize<WaterConsumtionJson>(json);
                var newValues = new List<WaterConsumptionValue>();
                foreach (var reading in values.readings) 
                {
                    newValues.Add(new WaterConsumptionValue()
                    {
                        Timestamp = (DateTime)reading[0],
                        Value = Convert.ToDecimal(reading[1])
                    });
                }

                if (newValues.Count == 0)
                    return null;

                newValues = newValues.OrderByDescending(x => x.Timestamp).ToList(); //latest is on top
                if (_consumption.Count == 0 || newValues[0].Value != _consumption[0].Value) 
                {
                    _consumption = newValues;
                    return newValues[0].Value;
                }
            }
            catch (Exception x)
            {
                Logger.LogError(x, $"Could not load water consumption from WebAPI");
                throw new Exception("Error loading Consumption from API");
            }
            return null;
        }
    }

    public class WaterConsumptionValue
    {
        public DateTime Timestamp { get; set; }
        public decimal Value { get; set; }
    }

    public class WaterConsumtionJson 
    {
        public string model { get; set; }
        public string serialNumber { get; set; }
        public List<List<object>> readings { get; set; }
    }
}
