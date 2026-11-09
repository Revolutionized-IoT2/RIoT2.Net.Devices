using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Govee;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="Govee"/> device's parameters: the Govee Open API key and which sensors to read.</summary>
    public sealed class GoveeOptions
    {
        public const string Fahrenheit = "fahrenheit";
        public const string Celsius = "celsius";

        [DeviceParameter("apiKey", Required = true, Secret = true, Description = "Govee Open API key, requested in the Govee Home app (Profile, Settings, Apply for API Key)")]
        public string ApiKey { get; set; } = "";

        [DeviceParameter("devices", Description = "Comma-separated Govee device ids or models (SKUs) to read; empty for every thermometer and hygrometer on the account")]
        public string Devices { get; set; } = "";

        [DeviceParameter("temperatureUnit", Default = Fahrenheit, Description = "The unit Govee sends temperatures in: fahrenheit (the Open API's) or celsius; reports are always °C")]
        public string TemperatureUnit { get; set; } = Fahrenheit;

        [DeviceParameter("endpoint", Description = "The Govee Open API URL; empty for https://openapi.api.govee.com")]
        public string Endpoint { get; set; } = "";
    }

    /// <summary>
    /// Govee Wi-Fi thermometers and hygrometers (a pool thermometer, for example) through the official Govee Open API.
    /// Reports each sensor's temperature (°C), humidity, battery and online state, and every finished hour's average,
    /// minimum and maximum temperature. Each sensor is a Matter temperature (and humidity) sensor.
    /// </summary>
    [RequiresHardware("Govee API")]
    public class Govee : AsyncDeviceBase<GoveeOptions>, IDeviceWithConfiguration, IAsyncCommandDevice, IMatterDevice
    {
        // Report keys; an address is "<Govee device id>|<key>".
        internal const string Temperature = "temperature";
        internal const string Humidity = "humidity";
        internal const string Battery = "battery";
        internal const string Online = "online";
        internal const string TemperatureHourly = "temperatureHourly";
        internal const string RefreshAddress = "refresh";

        /// <summary>The shortest time between two polls; Govee allows 30 state requests a minute per device.</summary>
        internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

        /// <summary>How often the device list is read again, for renamed, added or removed sensors.</summary>
        internal static readonly TimeSpan DeviceListInterval = TimeSpan.FromDays(1);

        /// <summary>How old a kept sample may be; older hours are dropped instead of reported.</summary>
        internal static readonly TimeSpan MaxSampleAge = TimeSpan.FromHours(48);

        // One poll at a time; the client isn't thread-safe.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _lock = new();
        private readonly Dictionary<string, GoveeSensorFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private GoveeClient? _client;
        // The sensors the last poll read, which the template lists; the template is built without I/O.
        private List<GoveeSensor> _sensors = [];
        private DateTimeOffset? _lastPoll;
        private DateTimeOffset? _listRead;
        private string[] _filter = [];
        private bool _fahrenheit = true;

        public Govee(ILogger logger) : base(logger) { }

        /// <summary>The folder of the sensor files; tests replace it.</summary>
        internal string DataDirectory { get; set; } = "Data";

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        // The HTTP call; the tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            RIoT2.Core.Utils.Web.SendAsync(request, cancellationToken: cancellationToken);

        public override void ConfigureDevice()
        {
            _client = null;
            _lastPoll = null;
            _listRead = null;
            _filter = (Options.Devices ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _fahrenheit = !string.Equals(Options.TemperatureUnit?.Trim(), GoveeOptions.Celsius, StringComparison.OrdinalIgnoreCase);
            lock (_lock)
            {
                _sensors = [];
                _files.Clear();
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            var problem = Problem();
            if (problem != null)
                throw new InvalidOperationException(problem);
            _client = new GoveeClient(Options.ApiKey.Trim(), Options.Endpoint, SendAsync);

            // The first poll's errors, a wrong API key too, put the device into the error state with the reason. A
            // configured device outlives a Govee outage at start: the next refresh polls again.
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoveeUnavailableException error) when (ReportTemplates?.Any() == true)
            {
                Logger.LogWarning("Govee is unavailable; the next refresh tries again: {Reason}", error.Message);
            }
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _client = null;
            return Task.CompletedTask;
        }

        private string? Problem()
        {
            var unit = Options.TemperatureUnit?.Trim();
            if (!string.IsNullOrEmpty(unit) && !string.Equals(unit, GoveeOptions.Fahrenheit, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(unit, GoveeOptions.Celsius, StringComparison.OrdinalIgnoreCase))
                return $"The temperatureUnit must be {GoveeOptions.Fahrenheit} or {GoveeOptions.Celsius}.";
            if (!string.IsNullOrWhiteSpace(Options.Endpoint)
                && (!Uri.TryCreate(Options.Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
                return "The endpoint must be an http or https URL.";
            return null;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshSensorsAsync(cancellationToken);

        private async Task RefreshSensorsAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumPollInterval)
            {
                Logger.LogDebug("Govee was polled less than a minute ago; the refresh is skipped.");
                return;
            }

            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GoveeUnavailableException error)
            {
                Logger.LogWarning("Could not refresh Govee: {Reason}", error.Message);
            }
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            if (!string.Equals(command.Address, RefreshAddress, StringComparison.OrdinalIgnoreCase))
                throw new UnknownCommandException(commandId);
            await RefreshSensorsAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The Govee device isn't started.");
            List<GoveeSensor> sensors;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = Time.GetUtcNow();
                lock (_lock)
                    sensors = _sensors;
                if (_listRead is not DateTimeOffset listRead || now - listRead >= DeviceListInterval)
                {
                    sensors = await ReadSensorsAsync(client, sensors, cancellationToken).ConfigureAwait(false);
                    _listRead = now;
                }

                foreach (var sensor in sensors)
                {
                    try
                    {
                        var state = await client.GetStateAsync(sensor, _fahrenheit, cancellationToken).ConfigureAwait(false);
                        // A sensor whose list entry didn't say what it measures shows it in its state.
                        sensor.HasTemperature |= state.Temperature != null;
                        sensor.HasHumidity |= state.Humidity != null;
                        sensor.State = state;
                    }
                    catch (GoveeRefusedException error)
                    {
                        Logger.LogWarning("Govee refused the state of {Sensor}: {Reason}", sensor.Name, error.Message);
                    }
                }
                _lastPoll = now;
                lock (_lock)
                    _sensors = sensors;
            }
            finally
            {
                _gate.Release();
            }

            foreach (var sensor in sensors.Where(x => x.State != null))
            {
                SendStateReports(sensor);
                SendHourlyReports(sensor);
            }
        }

        private async Task<List<GoveeSensor>> ReadSensorsAsync(GoveeClient client, List<GoveeSensor> previous, CancellationToken cancellationToken)
        {
            var devices = await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var device in devices)
                Logger.LogDebug("Govee device {Name}: id {Device}, model {Sku}, type {Type}, properties {Properties}", device.Name, device.Device, device.Sku,
                    device.Type, device.Properties.Count == 0 ? "-" : string.Join(", ", device.Properties));

            var sensors = _filter.Length == 0
                ? devices.Where(x => x.HasTemperature || x.HasHumidity).ToList()
                : devices.Where(x => _filter.Any(f => string.Equals(f, x.Device, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, x.Sku, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var sensor in sensors)
            {
                // Keep what the earlier states showed, until the next state.
                var known = previous.FirstOrDefault(x => string.Equals(x.Device, sensor.Device, StringComparison.OrdinalIgnoreCase));
                if (known == null)
                    continue;
                sensor.HasTemperature |= known.HasTemperature;
                sensor.HasHumidity |= known.HasHumidity;
                sensor.State = known.State;
            }

            if (sensors.Count == 0)
            {
                var found = devices.Count == 0 ? "none" : string.Join(", ", devices.Select(x => $"{x.Name} ({x.Sku}, {x.Device})"));
                Logger.LogWarning(_filter.Length == 0
                    ? "The Govee account has no thermometers or hygrometers the Open API reads. Devices found: {Devices}"
                    : "None of the configured Govee devices are on the account. Devices found: {Devices}", found);
            }
            return sensors;
        }

        internal static string Address(string device, string key) => $"{device}|{key}";

        private static (string? Device, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            return separator <= 0 ? (null, "") : (address![..separator], address[(separator + 1)..]);
        }

        private ReportTemplate? FindTemplate(string device, string key)
        {
            var address = Address(device, key);
            return ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
        }

        private GoveeSensor? FindSensor(string device)
        {
            lock (_lock)
                return _sensors.FirstOrDefault(x => string.Equals(x.Device, device, StringComparison.OrdinalIgnoreCase));
        }

        // While a sensor is offline Govee's values are history: only the online state is reported.
        private void SendStateReports(GoveeSensor sensor)
        {
            var state = sensor.State!;
            var now = Time.GetUtcNow().ToUnixTimeSeconds();
            (string Key, object? Value)[] values = state.Online == false
                ? [(Online, false)]
                : [(Online, state.Online), (Temperature, state.Temperature), (Humidity, state.Humidity), (Battery, state.Battery)];
            foreach (var (key, value) in values)
            {
                if (value == null || FindTemplate(sensor.Device, key) is not ReportTemplate template)
                    continue;
                SendReportIfValueChanged(this, new Report
                {
                    Id = template.Id,
                    TimeStamp = now,
                    Value = new ValueModel(value),
                    Filter = ""
                });
            }
        }

        // Each finished hour once, time-stamped to the hour's start. The samples of the open hour and the last reported
        // hour are kept in the sensor's file, so a restart neither loses an hour's samples nor repeats the hour.
        private void SendHourlyReports(GoveeSensor sensor)
        {
            if (FindTemplate(sensor.Device, TemperatureHourly) is not ReportTemplate template)
                return;

            var now = Time.GetUtcNow();
            var currentHour = HourOf(now);
            var file = LoadFile(sensor.Device);
            if (sensor.State is { Online: not false, Temperature: double temperature })
                file.Samples.Add(new GoveeSample { Time = now, Value = temperature });

            var hours = Hours(file.Samples, currentHour)
                .Where(x => file.LastReportedHour is not DateTimeOffset reported || x.Start > reported)
                .ToList();
            foreach (var hour in hours)
            {
                SendReport(this, new Report
                {
                    Id = template.Id,
                    TimeStamp = hour.Start.ToUnixTimeSeconds(),
                    Value = new ValueModel(new Dictionary<string, object>
                    {
                        ["average"] = hour.Average,
                        ["minimum"] = hour.Minimum,
                        ["maximum"] = hour.Maximum,
                        ["samples"] = hour.Samples
                    }),
                    Filter = ""
                });
            }
            if (hours.Count > 0)
                file.LastReportedHour = hours[^1].Start;
            file.Samples.RemoveAll(x => x.Time < currentHour || now - x.Time > MaxSampleAge);
            SaveFile(sensor.Device, file);
        }

        /// <summary>The finished hours (before <paramref name="currentHour"/>) of the samples, in time order.</summary>
        internal static List<GoveeHour> Hours(IEnumerable<GoveeSample> samples, DateTimeOffset currentHour) =>
            samples.Where(x => x.Time < currentHour)
                .GroupBy(x => HourOf(x.Time))
                .OrderBy(x => x.Key)
                .Select(x => new GoveeHour(x.Key, Math.Round(x.Average(s => s.Value), 2), x.Min(s => s.Value), x.Max(s => s.Value), x.Count()))
                .ToList();

        private static DateTimeOffset HourOf(DateTimeOffset time)
        {
            var utc = time.UtcDateTime;
            return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        }

        internal string SensorFile(string device)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(device.ToUpperInvariant()));
            return Path.Combine(DataDirectory, $"govee-{Convert.ToHexStringLower(hash)[..16]}.json");
        }

        private GoveeSensorFile LoadFile(string device)
        {
            lock (_lock)
            {
                if (_files.TryGetValue(device, out var cached))
                    return cached;
            }
            var path = SensorFile(device);
            GoveeSensorFile? file = null;
            try
            {
                if (File.Exists(path))
                    file = JsonSerializer.Deserialize<GoveeSensorFile>(File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Could not read the Govee sensor file {File}; its hourly temperatures start over.", path);
            }
            file ??= new GoveeSensorFile();
            file.Samples ??= [];
            lock (_lock)
                _files[device] = file;
            return file;
        }

        private void SaveFile(string device, GoveeSensorFile file)
        {
            var path = SensorFile(device);
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(file));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Could not save the Govee sensor file {File}.", path);
            }
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Govee",
                ClassFullName = GetType().FullName!,
                RefreshSchedule = "0 0/15 * ? * *", // every 15 minutes
                DeviceParameters = new Dictionary<string, string>
                {
                    ["apiKey"] = "",
                    ["devices"] = "",
                    ["temperatureUnit"] = GoveeOptions.Fahrenheit,
                    ["endpoint"] = ""
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            if (State != DeviceState.Running)
                return configuration;

            List<GoveeSensor> sensors;
            lock (_lock)
                sensors = _sensors.ToList();
            configuration.ReportTemplates = sensors.SelectMany(SensorReports).ToList();
            configuration.CommandTemplates =
            [
                new CommandTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Refresh Govee",
                    Address = RefreshAddress,
                    Type = ValueType.Boolean,
                    Model = true
                }
            ];
            return configuration;
        }

        private static IEnumerable<ReportTemplate> SensorReports(GoveeSensor sensor)
        {
            var celsius = ValueSchema.Number("°C", ValueSemantics.Temperature);
            if (sensor.HasTemperature)
            {
                yield return Report(sensor, Temperature, "temperature", ValueType.Number, sensor.State?.Temperature ?? 0d, celsius, "°C");
                yield return Report(sensor, TemperatureHourly, "hourly temperature", ValueType.Entity, null, ValueSchema.Entity(new Dictionary<string, ValueSchema>
                {
                    ["average"] = ValueSchema.Number("°C", ValueSemantics.Temperature),
                    ["minimum"] = ValueSchema.Number("°C", ValueSemantics.Temperature),
                    ["maximum"] = ValueSchema.Number("°C", ValueSemantics.Temperature),
                    ["samples"] = ValueSchema.Integer(null, null, 1)
                }), "°C");
            }
            if (sensor.HasHumidity)
                yield return Report(sensor, Humidity, "humidity", ValueType.Number, sensor.State?.Humidity ?? 0d,
                    ValueSchema.Number("%", ValueSemantics.Humidity, 0, 100), "%");
            if (sensor.BatteryInstance != null || sensor.State?.Battery != null)
                yield return Report(sensor, Battery, "battery", ValueType.Number, sensor.State?.Battery ?? 0d,
                    ValueSchema.Number("%", ValueSemantics.Battery, 0, 100), "%");
            yield return Report(sensor, Online, "online", ValueType.Boolean, sensor.State?.Online ?? true, ValueSchema.Boolean());
        }

        private static ReportTemplate Report(GoveeSensor sensor, string key, string name, ValueType type, object? model, ValueSchema schema, string? unit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(sensor.Device, key),
                Name = $"{sensor.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (unit != null)
                template.Parameters["unit"] = unit;
            return template;
        }

        /// <summary>
        /// Declares every sensor as a Matter temperature sensor, and a hygrometer also as a humidity sensor, on the RIoT
        /// Control Bridge.
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration handed in, because <see cref="GetConfigurationTemplate"/>
        /// mints new template ids on every call. The endpoint id comes from the Govee device id, so a sensor stays linked
        /// to the same Matter endpoint across restarts and re-imports.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.ReportTemplates == null)
                yield break;

            var devices = configuration.ReportTemplates.Select(x => SplitAddress(x.Address).Device).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var device in devices)
            {
                ReportTemplate? Find(string key) => configuration.ReportTemplates.FirstOrDefault(x => x.Address == Address(device, key));
                var sensor = FindSensor(device);
                var temperature = Find(Temperature);
                var name = sensor?.Name ?? (temperature?.Name is string reportName && reportName.EndsWith(" temperature", StringComparison.Ordinal)
                    ? reportName[..^" temperature".Length]
                    : "Govee sensor");
                var id = "govee:" + device.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();

                MatterEndpointTemplate Endpoint(string suffix, string what, MatterDeviceType type) => new()
                {
                    Id = suffix.Length == 0 ? id : $"{id}:{suffix}",
                    Name = what.Length == 0 ? name : $"{name} {what}",
                    DeviceType = type,
                    VendorName = "Govee",
                    ProductName = sensor?.Sku is { Length: > 0 } sku ? sku : "Govee sensor"
                };

                if (temperature != null)
                {
                    var endpoint = Endpoint("", "", MatterDeviceType.TemperatureSensor);
                    endpoint.Attributes.Add(Bind(MatterAttribute.TemperatureMeasuredValue, temperature, MatterValueScale.CelsiusToHundredths));
                    yield return endpoint;
                }
                if (Find(Humidity) is ReportTemplate humidity)
                {
                    var endpoint = Endpoint("humidity", "humidity", MatterDeviceType.HumiditySensor);
                    endpoint.Attributes.Add(Bind(MatterAttribute.HumidityMeasuredValue, humidity, MatterValueScale.PercentToHundredths));
                    yield return endpoint;
                }
            }
        }

        private static MatterAttributeBinding Bind(MatterAttribute attribute, ReportTemplate report, MatterValueScale scale) => new()
        {
            Attribute = attribute,
            ReportTemplateId = report.Id,
            Scale = scale
        };
    }
}
