using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Models.Matter;
using RIoT2.Core.Plugins;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.MelCloud;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="MelCloud"/> device's parameters: the MELCloud service and its account.</summary>
    public sealed class MelCloudOptions
    {
        public const string Classic = "classic";
        public const string Home = "home";

        [DeviceParameter("cloud", Default = Classic, Description = "classic (the MELCloud app, app.melcloud.com) or home (the MELCloud Home app)")]
        public string Cloud { get; set; } = Classic;

        [DeviceParameter("email", Required = true, Description = "E-mail address of the MELCloud account")]
        public string Email { get; set; } = "";

        [DeviceParameter("password", Required = true, Secret = true, Description = "Password of the MELCloud account")]
        public string Password { get; set; } = "";
    }

    /// <summary>
    /// Mitsubishi Electric air-to-air heat pumps through MELCloud, the cloud of their Wi-Fi adapters. Reports each
    /// unit's temperatures, power, mode, fan, vanes and energy; commands set them; each unit is a Matter thermostat.
    /// </summary>
    [RequiresHardware("MELCloud API")]
    public class MelCloud : AsyncDeviceBase<MelCloudOptions>, IDeviceWithConfiguration, IAsyncCommandDevice, IMatterDevice
    {
        // Report and command keys; an address is "<unit id>|<key>".
        internal const string RoomTemperature = "roomTemperature";
        internal const string SetTemperature = "setTemperature";
        internal const string Power = "power";
        internal const string Mode = "mode";
        internal const string ThermostatMode = "thermostatMode";
        internal const string FanSpeed = "fanSpeed";
        internal const string VaneVertical = "vaneVertical";
        internal const string VaneHorizontal = "vaneHorizontal";
        internal const string Offline = "offline";
        internal const string EnergyConsumed = "energyConsumed";
        internal const string EnergyToday = "energyToday";
        internal const string RefreshAddress = "refresh";

        /// <summary>The shortest time between two polls; MELCloud limits clients that poll more often.</summary>
        internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

        private static readonly int[] _matterModes =
            [MelCloudModes.MatterOff, MelCloudModes.MatterAuto, MelCloudModes.MatterCool, MelCloudModes.MatterHeat, MelCloudModes.MatterFanOnly, MelCloudModes.MatterDry];

        // One MELCloud call sequence at a time: a poll and a command don't interleave their requests.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _unitsLock = new();
        private IMelCloudBackend? _backend;
        // The units the last poll read, which the template lists; the template is built without I/O.
        private List<MelCloudUnit> _units = [];
        private DateTimeOffset? _lastPoll;

        public MelCloud(ILogger logger) : base(logger) { }

        /// <summary>The folder of the session files; tests replace it.</summary>
        internal string DataDirectory { get; set; } = "Data";

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        // The HTTP call; the tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            MelCloudHttp.SendAsync(request, cancellationToken);

        public override void ConfigureDevice()
        {
            _backend = null;
            _lastPoll = null;
            lock (_unitsLock)
                _units = [];
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            var cloud = Options.Cloud?.Trim().ToLowerInvariant();
            if (cloud is not (MelCloudOptions.Classic or MelCloudOptions.Home))
                throw new InvalidOperationException($"The cloud must be {MelCloudOptions.Classic} or {MelCloudOptions.Home}.");

            var email = Options.Email.Trim();
            var store = new MelCloudSessionStore(DataDirectory, cloud, email, Logger);
            _backend = cloud == MelCloudOptions.Home
                ? new MelCloudHomeBackend(email, Options.Password, SendAsync, store, Logger, Time)
                : new MelCloudClassicBackend(email, Options.Password, SendAsync, store, Logger);

            // The first poll's errors, a wrong password too, put the device into the error state with the reason. A
            // configured device outlives a MELCloud outage at start: the next refresh or command polls again.
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MelCloudUnavailableException error) when (ReportTemplates?.Any() == true || CommandTemplates?.Any() == true)
            {
                Logger.LogWarning("MELCloud is unavailable; the next refresh tries again: {Reason}", error.Message);
            }
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _backend = null;
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshUnitsAsync(cancellationToken);

        private async Task RefreshUnitsAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumPollInterval)
            {
                Logger.LogDebug("MELCloud was polled less than a minute ago; the refresh is skipped.");
                return;
            }

            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MelCloudUnavailableException error)
            {
                // MELCloud is often slow or limits requests for a while: keep the last state and try at the next refresh.
                Logger.LogWarning("Could not refresh MELCloud: {Reason}", error.Message);
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var backend = _backend ?? throw new DeviceNotReadyException("The MELCloud device isn't started.");
            IReadOnlyList<MelCloudUnit> units;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                units = await backend.GetUnitsAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            _lastPoll = Time.GetUtcNow();
            lock (_unitsLock)
            {
                // A value the poll didn't get (a meter without a new reading) keeps the one read before.
                foreach (var unit in units)
                {
                    var previous = _units.FirstOrDefault(x => x.Id == unit.Id);
                    unit.EnergyConsumed ??= previous?.EnergyConsumed;
                    unit.EnergyToday ??= previous?.EnergyToday;
                }
                _units = units.ToList();
            }

            if (units.Count == 0)
                Logger.LogWarning("The MELCloud account has no air-to-air units.");
            foreach (var unit in units)
                SendUnitReports(unit);
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            if (string.Equals(command.Address, RefreshAddress, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshUnitsAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var (unitId, key) = SplitAddress(command.Address);
            if (unitId == null)
                throw new UnknownCommandException(commandId);

            var backend = _backend;
            var unit = FindUnit(unitId);
            if (unit == null && backend != null && _lastPoll == null)
            {
                // MELCloud was unavailable at start: read the units now.
                await PollAsync(cancellationToken).ConfigureAwait(false);
                unit = FindUnit(unitId);
            }
            if (backend == null || unit == null)
                throw new DeviceNotReadyException($"MELCloud unit {unitId} isn't loaded yet.");

            var change = ParseCommand(key, value, unit) ?? throw new UnknownCommandException(commandId);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await backend.SetAsync(unit, change, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            // The unit takes the change from MELCloud within about a minute; report it now, the next poll confirms it.
            MelCloudUnit updated;
            lock (_unitsLock)
            {
                updated = (_units.FirstOrDefault(x => x.Id == unit.Id) ?? unit).Copy();
                updated.Apply(change);
                _units = _units.Select(x => x.Id == updated.Id ? updated : x).ToList();
            }
            SendUnitReports(updated);
        }

        /// <summary>The change a command asks for, or null for an unknown key. A value that doesn't fit throws.</summary>
        internal static MelCloudChange? ParseCommand(string key, string value, MelCloudUnit unit)
        {
            switch (key)
            {
                case SetTemperature:
                    var temperature = CommandNumber(value);
                    var step = unit.TemperatureStep > 0 ? unit.TemperatureStep : 0.5;
                    temperature = Math.Round(Math.Round(temperature / step, MidpointRounding.AwayFromZero) * step, 2);
                    if (temperature < unit.MinTemperature || temperature > unit.MaxTemperature)
                        throw new ArgumentOutOfRangeException(nameof(value), temperature,
                            $"The temperature must be {Format(unit.MinTemperature)}-{Format(unit.MaxTemperature)} °C.");
                    return new MelCloudChange { SetTemperature = temperature };
                case Power:
                    return new MelCloudChange { Power = CommandBool(value) };
                case Mode:
                    return new MelCloudChange
                    {
                        Mode = MelCloudModes.Find(MelCloudModes.All, CommandText(value))
                            ?? throw new ArgumentException($"The mode must be one of {string.Join(", ", MelCloudModes.All)}.", nameof(value))
                    };
                case ThermostatMode:
                    var number = CommandNumber(value);
                    if (number != Math.Floor(number))
                        throw new ArgumentException("The thermostat mode is a whole number.", nameof(value));
                    return MelCloudModes.FromMatter((int)number);
                case FanSpeed:
                    return new MelCloudChange { FanSpeed = Choice(MelCloudModes.FanSpeeds, value, "fan speed") };
                case VaneVertical:
                    return new MelCloudChange { VaneVertical = Choice(MelCloudModes.VerticalVanes, value, "vertical vane position") };
                case VaneHorizontal:
                    return new MelCloudChange { VaneHorizontal = Choice(MelCloudModes.HorizontalVanes, value, "horizontal vane position") };
                default:
                    return null;
            }
        }

        private static string Choice(IReadOnlyList<string> names, string value, string what) =>
            MelCloudModes.Find(names, CommandText(value))
                ?? throw new ArgumentException($"The {what} must be one of {string.Join(", ", names)}.", nameof(value));

        // A command value arrives as JSON: 22, "heat", true. A bare word is taken as text.
        private static JsonElement CommandJson(string value)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonSerializer.SerializeToElement(value);
            }
        }

        private static double CommandNumber(string value)
        {
            var json = CommandJson(value);
            if (json.ValueKind == JsonValueKind.Number)
                return json.GetDouble();
            if (json.ValueKind == JsonValueKind.String &&
                double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
            throw new ArgumentException($"'{value}' is not a number.", nameof(value));
        }

        private static bool CommandBool(string value)
        {
            var json = CommandJson(value);
            switch (json.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return json.GetDouble() != 0;
                case JsonValueKind.String:
                    var text = json.GetString()?.Trim().ToLowerInvariant();
                    if (text is "true" or "on" or "1")
                        return true;
                    if (text is "false" or "off" or "0")
                        return false;
                    break;
            }
            throw new ArgumentException($"'{value}' is not true or false.", nameof(value));
        }

        private static string CommandText(string value)
        {
            var json = CommandJson(value);
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString() ?? "",
                JsonValueKind.Number => json.GetDouble().ToString(CultureInfo.InvariantCulture),
                _ => value
            };
        }

        private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        internal static string Address(string unitId, string key) => $"{unitId}|{key}";

        private static (string? UnitId, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            return separator <= 0 ? (null, "") : (address![..separator], address[(separator + 1)..]);
        }

        private MelCloudUnit? FindUnit(string unitId)
        {
            lock (_unitsLock)
                return _units.FirstOrDefault(x => x.Id == unitId);
        }

        private void SendUnitReports(MelCloudUnit unit)
        {
            SendUnitReport(unit, RoomTemperature, unit.RoomTemperature);
            SendUnitReport(unit, SetTemperature, unit.SetTemperature);
            SendUnitReport(unit, Power, unit.Power);
            SendUnitReport(unit, Mode, unit.Mode);
            SendUnitReport(unit, ThermostatMode, unit.ThermostatMode);
            SendUnitReport(unit, FanSpeed, unit.FanSpeed);
            SendUnitReport(unit, VaneVertical, unit.VaneVertical);
            SendUnitReport(unit, VaneHorizontal, unit.VaneHorizontal);
            SendUnitReport(unit, Offline, unit.Offline);
            SendUnitReport(unit, EnergyConsumed, unit.EnergyConsumed);
            SendUnitReport(unit, EnergyToday, unit.EnergyToday);
        }

        private void SendUnitReport(MelCloudUnit unit, string key, object? value)
        {
            if (value == null)
                return;
            var address = Address(unit.Id, key);
            var template = ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
            if (template == null)
                return;

            // ValueModel(object) keeps a text such as the fan speed "3" a string.
            SendReportIfValueChanged(this, new Report
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(value),
                Filter = ""
            });
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "MELCloud",
                ClassFullName = GetType().FullName!,
                // Every hour, three minutes past. MELCloud limits frequent polling, and a unit uploads its state
                // only every few minutes anyway.
                RefreshSchedule = "0 3 * ? * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    ["cloud"] = MelCloudOptions.Classic,
                    ["email"] = "user@example.com",
                    ["password"] = ""
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            if (State != DeviceState.Running)
                return configuration;

            List<MelCloudUnit> units;
            lock (_unitsLock)
                units = _units.ToList();

            var reports = new List<ReportTemplate>();
            var commands = new List<CommandTemplate>();
            foreach (var unit in units)
            {
                reports.AddRange(UnitReports(unit));
                commands.AddRange(UnitCommands(unit));
            }
            commands.Add(new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh MELCloud",
                Address = RefreshAddress,
                Type = ValueType.Boolean,
                Model = true
            });
            configuration.ReportTemplates = reports;
            configuration.CommandTemplates = commands;
            return configuration;
        }

        private static IEnumerable<ReportTemplate> UnitReports(MelCloudUnit unit)
        {
            yield return Report(unit, RoomTemperature, "room temperature", ValueType.Number, unit.RoomTemperature ?? 21,
                ValueSchema.Number("°C", ValueSemantics.Temperature), "°C");
            yield return Report(unit, SetTemperature, "target temperature", ValueType.Number, unit.SetTemperature ?? 21,
                ValueSchema.Number("°C", ValueSemantics.Temperature, unit.MinTemperature, unit.MaxTemperature), "°C");
            yield return Report(unit, Power, "power", ValueType.Boolean, unit.Power ?? false, ValueSchema.Boolean(ValueSemantics.Switch));
            yield return Report(unit, Mode, "mode", ValueType.Text, unit.Mode ?? MelCloudModes.Heat, Choices(MelCloudModes.All));
            yield return Report(unit, ThermostatMode, "thermostat mode", ValueType.Number, unit.ThermostatMode ?? MelCloudModes.MatterHeat, MatterModes());
            yield return Report(unit, FanSpeed, "fan speed", ValueType.Text, unit.FanSpeed ?? "auto", Choices(MelCloudModes.FanSpeeds));
            yield return Report(unit, VaneVertical, "vertical vane", ValueType.Text, unit.VaneVertical ?? "auto", Choices(MelCloudModes.VerticalVanes));
            yield return Report(unit, VaneHorizontal, "horizontal vane", ValueType.Text, unit.VaneHorizontal ?? "auto", Choices(MelCloudModes.HorizontalVanes));
            if (unit.Offline != null)
                yield return Report(unit, Offline, "offline", ValueType.Boolean, unit.Offline.Value, ValueSchema.Boolean());
            if (unit.EnergyConsumed != null)
                yield return Report(unit, EnergyConsumed, "energy consumed", ValueType.Number, unit.EnergyConsumed.Value,
                    ValueSchema.Number("kWh", ValueSemantics.Energy, 0), "kWh");
            if (unit.EnergyToday != null)
                yield return Report(unit, EnergyToday, "energy today", ValueType.Number, unit.EnergyToday.Value,
                    ValueSchema.Number("kWh", ValueSemantics.Energy, 0), "kWh");
        }

        private static IEnumerable<CommandTemplate> UnitCommands(MelCloudUnit unit)
        {
            yield return Command(unit, SetTemperature, "set temperature", ValueType.Number, unit.SetTemperature ?? 21,
                ValueSchema.Number("°C", ValueSemantics.Temperature, unit.MinTemperature, unit.MaxTemperature));
            yield return Command(unit, Power, "set power", ValueType.Boolean, true, ValueSchema.Boolean(ValueSemantics.Switch));
            yield return Command(unit, Mode, "set mode", ValueType.Text, MelCloudModes.Heat, Choices(MelCloudModes.All));
            yield return Command(unit, ThermostatMode, "set thermostat mode", ValueType.Number, MelCloudModes.MatterHeat, MatterModes());
            yield return Command(unit, FanSpeed, "set fan speed", ValueType.Text, "auto", Choices(MelCloudModes.FanSpeeds));
            yield return Command(unit, VaneVertical, "set vertical vane", ValueType.Text, "auto", Choices(MelCloudModes.VerticalVanes));
            yield return Command(unit, VaneHorizontal, "set horizontal vane", ValueType.Text, "auto", Choices(MelCloudModes.HorizontalVanes));
        }

        private static ReportTemplate Report(MelCloudUnit unit, string key, string name, ValueType type, object model, ValueSchema schema, string? valueUnit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(unit.Id, key),
                Name = $"{unit.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (valueUnit != null)
                template.Parameters["unit"] = valueUnit;
            return template;
        }

        private static CommandTemplate Command(MelCloudUnit unit, string key, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = Address(unit.Id, key),
            Name = $"{unit.Name} {name}",
            Type = type,
            Model = model,
            Schema = schema
        };

        private static ValueSchema Choices(IReadOnlyList<string> names)
        {
            var schema = ValueSchema.Text();
            schema.Enum = names.Cast<object>().ToList();
            return schema;
        }

        private static ValueSchema MatterModes()
        {
            var schema = ValueSchema.Integer();
            schema.Description = "Matter thermostat SystemMode: 0 off, 1 auto, 3 cool, 4 heat, 7 fan, 8 dry";
            schema.Enum = _matterModes.Cast<object>().ToList();
            return schema;
        }

        /// <summary>
        /// Declares every unit as a Matter thermostat on the RIoT Control Bridge. The local temperature, both
        /// setpoints and the system mode follow the unit's reports; a controller's setpoint or mode change sends the
        /// set temperature or thermostat mode command.
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration handed in, because <see cref="GetConfigurationTemplate"/>
        /// mints new template ids on every call. The endpoint id comes from the MELCloud unit id, so a unit stays
        /// linked to the same Matter endpoint across restarts and re-imports. The bridge's thermostat has no Auto
        /// mode: a unit in auto mode shows its setpoints but not the mode.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.CommandTemplates == null || configuration.ReportTemplates == null)
                yield break;

            foreach (var setpoint in configuration.CommandTemplates)
            {
                var (unitId, key) = SplitAddress(setpoint.Address);
                if (unitId == null || key != SetTemperature)
                    continue;

                var room = FindReport(configuration, unitId, RoomTemperature);
                var target = FindReport(configuration, unitId, SetTemperature);
                var mode = FindReport(configuration, unitId, ThermostatMode);
                var modeCommand = configuration.CommandTemplates.FirstOrDefault(x => x.Address == Address(unitId, ThermostatMode));
                if (room == null || target == null)
                    continue;

                var endpoint = new MatterEndpointTemplate
                {
                    Id = $"melcloud:{unitId}",
                    Name = FindUnit(unitId)?.Name ?? setpoint.Name,
                    DeviceType = MatterDeviceType.Thermostat,
                    VendorName = "Mitsubishi Electric",
                    ProductName = "MELCloud air-to-air unit"
                };
                endpoint.Attributes.Add(Attribute(MatterAttribute.LocalTemperature, room.Id, MatterValueScale.CelsiusToHundredths));
                endpoint.Attributes.Add(Attribute(MatterAttribute.OccupiedHeatingSetpoint, target.Id, MatterValueScale.CelsiusToHundredths));
                endpoint.Attributes.Add(Attribute(MatterAttribute.OccupiedCoolingSetpoint, target.Id, MatterValueScale.CelsiusToHundredths));
                endpoint.Commands.Add(new MatterCommandBinding
                {
                    Attribute = MatterAttribute.OccupiedHeatingSetpoint, CommandTemplateId = setpoint.Id, Scale = MatterValueScale.CelsiusToHundredths
                });
                endpoint.Commands.Add(new MatterCommandBinding
                {
                    Attribute = MatterAttribute.OccupiedCoolingSetpoint, CommandTemplateId = setpoint.Id, Scale = MatterValueScale.CelsiusToHundredths
                });
                if (mode != null)
                    endpoint.Attributes.Add(Attribute(MatterAttribute.ThermostatSystemMode, mode.Id, MatterValueScale.None));
                if (modeCommand != null)
                    endpoint.Commands.Add(new MatterCommandBinding { Attribute = MatterAttribute.ThermostatSystemMode, CommandTemplateId = modeCommand.Id });
                yield return endpoint;
            }
        }

        private static ReportTemplate? FindReport(DeviceConfiguration configuration, string unitId, string key) =>
            configuration.ReportTemplates.FirstOrDefault(x => x.Address == Address(unitId, key));

        private static MatterAttributeBinding Attribute(MatterAttribute attribute, string reportTemplateId, MatterValueScale scale) => new()
        {
            Attribute = attribute,
            ReportTemplateId = reportTemplateId,
            Scale = scale
        };
    }
}
