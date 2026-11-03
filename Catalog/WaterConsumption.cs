using RIoT2.Core.Plugins;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using System.Net.Http.Headers;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="WaterConsumption"/> device's parameters.</summary>
    public sealed class WaterConsumptionOptions
    {
        [DeviceParameter("securityToken", Required = true, Secret = true, Description = "Water meter API token")]
        public string SecurityToken { get; set; } = "";

        [DeviceParameter("endpoint", Default = "https://wmd.wrm-systems.fi/api/watermeter", Description = "Water meter API URL")]
        public string Endpoint { get; set; } = "";
    }

    [RequiresHardware("Water meter API")]
    public class WaterConsumption : AsyncDeviceBase<WaterConsumptionOptions>, IDeviceWithConfiguration
    {
        public WaterConsumption(ILogger logger) : base(logger) { }

        private string? _securityToken = "";
        private string? _endpoint = "";
        private List<WaterConsumptionValue>? _consumption;

        public override void ConfigureDevice()
        {
            _securityToken = Options.SecurityToken;
            _endpoint = Options.Endpoint;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : SendLatestValueAsync(cancellationToken);

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Water Consumption";
            deviceConfiguration.RefreshSchedule = "0 * * ? * *"; //every hour
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("securityToken", Guid.NewGuid().ToString());
            deviceConfiguration.DeviceParameters.Add("endpoint", "https://wmd.wrm-systems.fi/api/watermeter");

            deviceConfiguration.ClassFullName = this.GetType().FullName!;
            var reportConfigurations = new List<ReportTemplate>();

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "watermeter",
                Name = "Water consumption",
                Type = Core.ValueType.Number,
                Parameters = new Dictionary<string, string>() { { "unit", "m3" }, {"precision", "3" } }
            });

            deviceConfiguration.CommandTemplates = null;
            deviceConfiguration.ReportTemplates = reportConfigurations;
            return deviceConfiguration;
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => SendLatestValueAsync(cancellationToken);

        private async Task SendLatestValueAsync(CancellationToken cancellationToken)
        {
            var template = getReportTemplate();
            if (template == null)
                return;

            var latestValue = await getLatestValue(cancellationToken).ConfigureAwait(false);
            if (latestValue == null)
                return;

            int precision = 3; //default
            if (template.Parameters != null && template.Parameters.ContainsKey("precision"))
                precision = int.Parse(template.Parameters["precision"]);

            var rounded = Math.Round(latestValue.Value, precision);

            SendReport(this, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(rounded),
                Filter = ""
            });
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _consumption = null;
            return Task.CompletedTask;
        }

        // The HTTP call; the lifecycle tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Core.Utils.Web.SendAsync(request, cancellationToken: cancellationToken);

        private ReportTemplate? getReportTemplate()
        {
            return ReportTemplates?.FirstOrDefault(x => x.Address == "watermeter");
        }

        private async Task<decimal?> getLatestValue(CancellationToken cancellationToken)
        {
            if (_consumption == null)
                _consumption = new List<WaterConsumptionValue>();

            try
            {
                var url = _endpoint + $"?startDate={DateTime.Now.ToString("yyyy-MM-dd")}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _securityToken);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                // An empty or "null" body fails here and is wrapped below, as before.
                var values = Json.Deserialize<WaterConsumtionJson>(json)!;
                var newValues = new List<WaterConsumptionValue>();
                foreach (var reading in values.readings)
                {
                    newValues.Add(new WaterConsumptionValue()
                    {
                        Timestamp = Convert.ToDateTime(reading[0]),
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Could not load water consumption from WebAPI");
                throw new Exception("Error loading Consumption from API", x);
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
        // Set by the JSON deserializer; a valid payload carries every member.
        public string model { get; set; } = null!;
        public string serialNumber { get; set; } = null!;
        public List<List<object>> readings { get; set; } = null!;
    }
}
