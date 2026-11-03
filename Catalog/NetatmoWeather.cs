using RIoT2.Core.Plugins;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;
using System.Reflection;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="NetatmoWeather"/> device's parameters: the Netatmo app and the station.</summary>
    public sealed class NetatmoWeatherOptions : NetatmoOptions
    {
        [DeviceParameter("stationId", Description = "MAC address of the weather station")]
        public string StationId { get; set; } = "";
    }

    [RequiresHardware("Netatmo API")]
    public class NetatmoWeather : NetatmoBase<NetatmoWeatherOptions>, IDeviceWithConfiguration
    {
        private string? _stationId;
        // The last station data, which the template lists; the template is built without I/O.
        private NetatmoStationsData? _stationsData;

        public NetatmoWeather(ILogger logger, INetatmoAuthClientFactory authClients) : base(logger, authClients) { }

        public override void ConfigureDevice()
        {
            _stationId = Options.StationId;
            _stationsData = null;

            ConfigureNetatmo();
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => generateWeatherReports(cancellationToken);

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : generateWeatherReports(cancellationToken);

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Netatmo Weather";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>
            {
                { "token", "123ecasc" },
                { "refresh_token", "mnbbcc123" },
                { "clientId", "clientid" },
                { "clientSecret", "adfdfd-dfdf-df" },
                { "stationId", "a0:44:23:b5:b7" }
            };

            deviceConfiguration.ClassFullName = this.GetType().FullName!;
            deviceConfiguration.RefreshSchedule = "0 0/15 0 ? * * *";

            var reportConfigurations = new List<ReportTemplate>();

            if(State != Core.DeviceState.Running)
                return deviceConfiguration;

            var data = _stationsData;

            if (data == null || data.status != "ok")
                return deviceConfiguration;

            foreach (var d in data.body.devices)
            {
                var mainModuleData = d.dashboard_data;
                foreach (PropertyInfo prop in mainModuleData.GetType().GetProperties()) 
                {
                    var value = prop.GetValue(mainModuleData);
                    reportConfigurations.Add(new ReportTemplate()
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = $"{d._id}|{prop.Name}",
                        Name = prop.Name,
                        // A missing trend is null, which GetObjectValueType types as Entity.
                        Type = GetObjectValueType(value!),
                        Model = value
                    });
                }

                foreach (var subModule in d.modules)
                {
                    var subModuleData = subModule.dashboard_data;
                    foreach (PropertyInfo prop in subModuleData.GetType().GetProperties()) 
                    {
                        var value = prop.GetValue(subModuleData);
                        reportConfigurations.Add(new ReportTemplate()
                        {
                            Id = Guid.NewGuid().ToString(),
                            Address = $"{subModule._id}|{prop.Name}",
                            Name = prop.Name,
                            Type = GetObjectValueType(value!),
                            Model = value
                        });
                    }
                }
            }

            deviceConfiguration.CommandTemplates = null;
            deviceConfiguration.ReportTemplates = reportConfigurations;
            return deviceConfiguration;
        }

        private async Task generateWeatherReports(CancellationToken cancellationToken)
        {
            var data = await GetNetatmoStationsData(_stationId, cancellationToken).ConfigureAwait(false);
            if (data == null || data.status.ToLower() != "ok")
                return;

            _stationsData = data;

            foreach (var d in data.body.devices)
            {
                var mainModuleData = d.dashboard_data;
                foreach (PropertyInfo prop in mainModuleData.GetType().GetProperties())
                    SendNetatmoReport(this, $"{d._id}|{prop.Name}".ToLower(), prop.GetValue(mainModuleData));

                foreach (var subModule in d.modules)
                {
                    var subModuleData = subModule.dashboard_data;
                    foreach (PropertyInfo prop in subModuleData.GetType().GetProperties())
                        SendNetatmoReport(this, $"{subModule._id}|{prop.Name}".ToLower(), prop.GetValue(subModuleData));
                }
            }
        }
    }
}
