using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Net.Devices.Abstracts;
using System.Reflection;

namespace RIoT2.Net.Devices.Catalog
{
    internal class NetatmoWeather : NetatmoBase, IDeviceWithConfiguration, IRefreshableReportDevice
    {
        private string _stationId;

        internal NetatmoWeather(ILogger logger) : base(logger) { }

        public override void ConfigureDevice()
        {
            string token = GetConfiguration<string>("token");
            string refreshToken = GetConfiguration<string>("refresh_token");
            string clientId = GetConfiguration<string>("clientId");
            string clientSecret = GetConfiguration<string>("clientSecret");
            _stationId = GetConfiguration<string>("stationId");

            ConfigureNetatmo(token, refreshToken, clientId, clientSecret);
        }

        public override void StartDevice()
        {
            generateWeatherReports().Wait();
        }
        public override void StopDevice()
        {
            //no actions needed
        }

        public override void Refresh(ReportTemplate report)
        {
            if (report != null) //report should be null because in this case, refresh is defined on device level
                return;

            generateWeatherReports().Wait();
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Netatmo Weather device";
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>
            {
                { "token", "123ecasc" },
                { "refresh_token", "mnbbcc123" },
                { "clientId", "clientid" },
                { "clientSecret", "adfdfd-dfdf-df" },
                { "stationId", "a0:44:23:b5:b7" }
            };

            deviceConfiguration.ClassFullName = this.GetType().FullName;
            deviceConfiguration.RefreshSchedule = "0 0/15 0 ? * * *";

            var reportConfigurations = new List<ReportTemplate>();

            if(State != Core.DeviceState.Running)
                return deviceConfiguration;

            var data = GetNetatmoStationsData(_stationId).Result;

            if (data == null || data.status != "ok")
                return deviceConfiguration;

            foreach (var d in data.body.devices)
            {
                var mainModuleData = d.dashboard_data;
                foreach (PropertyInfo prop in mainModuleData.GetType().GetProperties()) 
                {
                    reportConfigurations.Add(new ReportTemplate()
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = $"{d._id}|{prop.Name}",
                        Name = prop.Name,
                        Type = GetObjectValueType(prop.GetValue(mainModuleData))
                    });
                }

                foreach (var subModule in d.modules)
                {
                    var subModuleData = subModule.dashboard_data;
                    foreach (PropertyInfo prop in subModuleData.GetType().GetProperties()) 
                    {
                        reportConfigurations.Add(new ReportTemplate()
                        {
                            Id = Guid.NewGuid().ToString(),
                            Address = $"{subModule._id}|{prop.Name}",
                            Name = prop.Name,
                            Type = GetObjectValueType(prop.GetValue(subModuleData))
                        });
                    }
                }
            }

            deviceConfiguration.CommandTemplates = null;
            deviceConfiguration.ReportTemplates = reportConfigurations;
            return deviceConfiguration;
        }

        private async Task generateWeatherReports() 
        {
            var data = await GetNetatmoStationsData(_stationId);
            if (data == null || data.status.ToLower() != "ok")
                return;

            foreach (var d in data.body.devices)
            {
                var mainModuleData = d.dashboard_data;
                foreach (PropertyInfo prop in mainModuleData.GetType().GetProperties())
                    SendNetatmoReport(this, $"{d._id}|{prop.Name}", prop.GetValue(mainModuleData));

                foreach (var subModule in d.modules)
                {
                    var subModuleData = subModule.dashboard_data;
                    foreach (PropertyInfo prop in subModuleData.GetType().GetProperties())
                        SendNetatmoReport(this, $"{subModule._id}|{prop.Name}", prop.GetValue(subModuleData));
                }
            }
        }
    }
}
