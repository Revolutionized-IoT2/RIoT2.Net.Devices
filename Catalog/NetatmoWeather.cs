using Microsoft.Extensions.Logging;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="NetatmoWeather"/> device's parameters: the Netatmo app, the station and the coaches.</summary>
    public sealed class NetatmoWeatherOptions : NetatmoOptions
    {
        [DeviceParameter("stationId", Description = "MAC address of the weather station; empty: every station of the account")]
        public string StationId { get; set; } = "";

        [DeviceParameter("includeHomeCoach", Default = "false", Description = "Also report the account's Healthy Home Coaches")]
        public bool IncludeHomeCoach { get; set; }
    }

    /// <summary>
    /// Netatmo weather stations and Healthy Home Coaches. Netatmo pushes nothing for them, and a station uploads about
    /// every ten minutes, so the device polls and skips a module whose upload it already reported. Each module
    /// reports what it measures (its <c>data_type</c>), and its battery, reachability and signal.
    /// </summary>
    [RequiresHardware("Netatmo API")]
    public class NetatmoWeather : NetatmoBase<NetatmoWeatherOptions>, IDeviceWithConfiguration
    {
        /// <summary>The measurements of a data type: report address suffix, value type and how to read it.</summary>
        internal sealed record Field(string Name, Core.ValueType Type, Func<DashboardData, object?> Read);

        internal static readonly IReadOnlyDictionary<string, Field[]> FieldsByDataType = new Dictionary<string, Field[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Temperature"] =
            [
                new("Temperature", Core.ValueType.Number, d => d.Temperature),
                new("min_temp", Core.ValueType.Number, d => d.min_temp),
                new("max_temp", Core.ValueType.Number, d => d.max_temp),
                new("temp_trend", Core.ValueType.Text, d => d.temp_trend)
            ],
            ["CO2"] = [new("CO2", Core.ValueType.Number, d => d.CO2)],
            ["Humidity"] = [new("Humidity", Core.ValueType.Number, d => d.Humidity)],
            ["Noise"] = [new("Noise", Core.ValueType.Number, d => d.Noise)],
            ["Pressure"] =
            [
                new("Pressure", Core.ValueType.Number, d => d.Pressure),
                new("AbsolutePressure", Core.ValueType.Number, d => d.AbsolutePressure),
                new("pressure_trend", Core.ValueType.Text, d => d.pressure_trend)
            ],
            ["Rain"] =
            [
                new("Rain", Core.ValueType.Number, d => d.Rain),
                new("sum_rain_1", Core.ValueType.Number, d => d.sum_rain_1),
                new("sum_rain_24", Core.ValueType.Number, d => d.sum_rain_24)
            ],
            ["Wind"] =
            [
                new("WindStrength", Core.ValueType.Number, d => d.WindStrength),
                new("WindAngle", Core.ValueType.Number, d => d.WindAngle),
                new("GustStrength", Core.ValueType.Number, d => d.GustStrength),
                new("GustAngle", Core.ValueType.Number, d => d.GustAngle),
                new("max_wind_str", Core.ValueType.Number, d => d.max_wind_str)
            ],
            ["health_idx"] = [new("health_idx", Core.ValueType.Number, d => d.health_idx)]
        };

        private readonly Dictionary<string, long> _lastUploads = new(StringComparer.Ordinal);
        // What the last read returned, which the template lists; the template is built without I/O.
        private List<StationDevice> _devices = [];

        public NetatmoWeather(ILogger logger, INetatmoAuthClientFactory authClients) : base(logger, authClients) { }

        protected override IReadOnlyCollection<string> Scopes =>
            Options.IncludeHomeCoach ? ["read_station", "read_homecoach"] : ["read_station"];

        public override void ConfigureDevice()
        {
            ConfigureNetatmo();
            _devices = [];
            _lastUploads.Clear();
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => CallNetatmoAsync(ReadAsync, cancellationToken);

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null || IsBackingOff ? Task.CompletedTask : CallNetatmoAsync(ReadAsync, cancellationToken);

        protected override Task OnConnectedAsync(CancellationToken cancellationToken) => CallNetatmoAsync(ReadAsync, cancellationToken);

        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            var stations = await Api.GetAsync<NetatmoStationsData>("api/getstationsdata",
                [KeyValuePair.Create("device_id", Options.StationId?.Trim() ?? "")], cancellationToken).ConfigureAwait(false);
            Api.Auth.Account = stations?.body?.user?.mail;
            var devices = stations?.body?.devices?.ToList() ?? [];

            if (Options.IncludeHomeCoach)
            {
                var coaches = await Api.GetAsync<NetatmoStationsData>("api/gethomecoachsdata", null, cancellationToken).ConfigureAwait(false);
                Api.Auth.Account = coaches?.body?.user?.mail;
                devices.AddRange(coaches?.body?.devices ?? []);
            }

            _devices = devices;
            foreach (var device in devices)
            {
                Report(device, device.wifi_status);
                foreach (var module in device.modules ?? [])
                    Report(module, null);
            }
        }

        private void Report(StationModule module, int? wifi)
        {
            var data = module.dashboard_data;
            var uploaded = data?.time_utc;
            var fresh = uploaded == null || !_lastUploads.TryGetValue(module._id, out var last) || last != uploaded;
            if (data != null && fresh)
            {
                foreach (var field in FieldsOf(module))
                    SendState($"{module._id}|{field.Name}", field.Read(data), uploaded);
                if (uploaded != null)
                    _lastUploads[module._id] = uploaded.Value;
            }

            SendState($"{module._id}|reachable", module.reachable);
            SendState($"{module._id}|battery", module.battery_percent);
            SendState($"{module._id}|signal", wifi ?? module.rf_status);
        }

        /// <summary>The fields a module measures: from its <c>data_type</c>, or from the values it has when it lists none.</summary>
        internal static IEnumerable<Field> FieldsOf(StationModule module)
        {
            var types = module.data_type?.ToList() ?? [];
            if (string.Equals(module.type, "NHC", StringComparison.OrdinalIgnoreCase) && !types.Contains("health_idx", StringComparer.OrdinalIgnoreCase))
                types.Add("health_idx");
            if (types.Count > 0)
                return types.SelectMany(type => FieldsByDataType.TryGetValue(type, out var fields) ? fields : []);

            var data = module.dashboard_data;
            return data == null ? [] : FieldsByDataType.Values.SelectMany(fields => fields).Where(field => field.Read(data) != null);
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Netatmo Weather",
                ClassFullName = GetType().FullName!,
                RefreshSchedule = "0 0/5 * ? * * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    { "clientId", "" },
                    { "clientSecret", "" },
                    { "token", "" },
                    { "refresh_token", "" },
                    { "stationId", "" },
                    { "includeHomeCoach", "false" }
                },
                CommandTemplates = null,
                ReportTemplates = []
            };

            if (State != Core.DeviceState.Running)
                return deviceConfiguration;

            foreach (var device in _devices)
            {
                var station = device.station_name ?? device.module_name ?? device._id;
                Templates(deviceConfiguration.ReportTemplates, device, device.module_name ?? station, device.wifi_status);
                foreach (var module in device.modules ?? [])
                    Templates(deviceConfiguration.ReportTemplates, module, module.module_name ?? module._id, null);
            }
            return deviceConfiguration;
        }

        private static void Templates(List<ReportTemplate> templates, StationModule module, string name, int? wifi)
        {
            foreach (var field in FieldsOf(module))
            {
                var value = module.dashboard_data == null ? null : field.Read(module.dashboard_data);
                templates.Add(Template($"{module._id}|{field.Name}", $"{name} {field.Name}", field.Type,
                    value ?? (field.Type == Core.ValueType.Number ? 0 : "")));
            }
            templates.Add(Template($"{module._id}|reachable", $"{name} reachable", Core.ValueType.Boolean, module.reachable ?? true));
            if (module.battery_percent != null)
                templates.Add(Template($"{module._id}|battery", $"{name} battery", Core.ValueType.Number, module.battery_percent));
            if ((wifi ?? module.rf_status) is int signal)
                templates.Add(Template($"{module._id}|signal", $"{name} signal", Core.ValueType.Number, signal));
        }

        private static ReportTemplate Template(string address, string name, Core.ValueType type, object model) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = address,
            Name = name,
            Type = type,
            Model = model
        };
    }
}
