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
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Themo;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="Themo"/> device's parameters: the Themo app's account.</summary>
    public sealed class ThemoOptions
    {
        [DeviceParameter("email", Required = true, Description = "E-mail address of the Themo account")]
        public string Email { get; set; } = "";

        [DeviceParameter("password", Required = true, Secret = true, Description = "Password of the Themo account")]
        public string Password { get; set; } = "";
    }

    /// <summary>
    /// Themo smart thermostats through the Themo Public API. Reports each thermostat's temperatures, target, mode,
    /// heating state, settings, active schedule and hourly energy; commands set the target, mode, schedule, backlight,
    /// child lock, frost protection and timer boost. Each thermostat is a Matter thermostat.
    /// </summary>
    [RequiresHardware("Themo API")]
    public class Themo : AsyncDeviceBase<ThemoOptions>, IDeviceWithConfiguration, IAsyncCommandDevice, IMatterDevice
    {
        // Report and command keys; an address is "<thermostat id>|<key>".
        internal const string Temperature = "temperature";
        internal const string RoomTemperature = "roomTemperature";
        internal const string FloorTemperature = "floorTemperature";
        internal const string OutsideTemperature = "outsideTemperature";
        internal const string SetTemperature = "setTemperature";
        internal const string Mode = "mode";
        internal const string ThermostatMode = "thermostatMode";
        internal const string Heating = "heating";
        internal const string Connected = "connected";
        internal const string Lights = "lights";
        internal const string Lock = "lock";
        internal const string FrostProtection = "frostProtection";
        internal const string FrostTemperature = "frostTemperature";
        internal const string Schedule = "schedule";
        internal const string Boost = "boost";
        internal const string EnergyHour = "energyHour";
        internal const string EnergyHourly = "energyHourly";
        internal const string EnergyToday = "energyToday";
        internal const string HeatingShareToday = "heatingShareToday";
        internal const string RefreshAddress = "refresh";

        internal const double MinTemperature = 5;
        internal const double MaxTemperature = 35;
        internal const double MinFrostTemperature = 5;
        internal const double MaxFrostTemperature = 15;
        internal const int MaxBoostMinutes = 1440;

        /// <summary>How many finished hours the energy reports cover, and how many missed hours a restart catches up.</summary>
        internal const int EnergyHours = 48;

        /// <summary>The shortest time between two polls.</summary>
        internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

        /// <summary>How often the schedules are read.</summary>
        internal static readonly TimeSpan ScheduleInterval = TimeSpan.FromDays(1);

        // CManTimer needs software 4.2, the timer boost lock 4.4.0.
        private static readonly Version _boostSoftware = new(4, 2);
        private static readonly Version _boostLockSoftware = new(4, 4, 0);
        private static readonly int[] _matterModes = [ThemoModes.MatterOff, ThemoModes.MatterAuto, ThemoModes.MatterHeat];

        // One Themo call sequence at a time: a poll and a command don't interleave, and the client isn't thread-safe.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _lock = new();
        private readonly Dictionary<int, DateTimeOffset> _lastReportedHours = [];
        private readonly Dictionary<string, string> _publishedSeries = [];
        private ThemoClient? _client;
        // The thermostats the last poll read, which the template lists; the template is built without I/O.
        private List<ThemoThermostat> _thermostats = [];
        private DateTimeOffset? _lastPoll;
        private DateTimeOffset? _schedulesRead;
        private bool _energyWarned;

        public Themo(ILogger logger) : base(logger) { }

        /// <summary>The folder of the energy state files; tests replace it.</summary>
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
            _schedulesRead = null;
            _energyWarned = false;
            lock (_lock)
            {
                _thermostats = [];
                _lastReportedHours.Clear();
                _publishedSeries.Clear();
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _client = new ThemoClient(Options.Email.Trim(), Options.Password, SendAsync, Logger);

            // The first poll's errors, a wrong password too, put the device into the error state with the reason. A
            // configured device outlives a Themo outage at start: the next refresh or command polls again.
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ThemoUnavailableException error) when (ReportTemplates?.Any() == true || CommandTemplates?.Any() == true)
            {
                Logger.LogWarning("Themo is unavailable; the next refresh tries again: {Reason}", error.Message);
            }
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _client = null;
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshThermostatsAsync(cancellationToken);

        private async Task RefreshThermostatsAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumPollInterval)
            {
                Logger.LogDebug("Themo was polled less than a minute ago; the refresh is skipped.");
                return;
            }

            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ThemoUnavailableException error)
            {
                Logger.LogWarning("Could not refresh Themo: {Reason}", error.Message);
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The Themo device isn't started.");
            List<ThemoThermostat> thermostats;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                thermostats = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            _lastPoll = Time.GetUtcNow();
            lock (_lock)
                _thermostats = thermostats;

            if (thermostats.Count == 0)
                Logger.LogWarning("The Themo account has no thermostats.");
            foreach (var thermostat in thermostats)
            {
                SendStateReports(thermostat);
                SendEnergyReports(thermostat);
            }
        }

        private async Task<List<ThemoThermostat>> ReadAsync(ThemoClient client, CancellationToken cancellationToken)
        {
            List<ThemoThermostat> previous;
            lock (_lock)
                previous = _thermostats.ToList();

            var now = Time.GetUtcNow();
            var readSchedules = _schedulesRead is not DateTimeOffset readAt || now - readAt >= ScheduleInterval;
            var thermostats = new List<ThemoThermostat>();
            foreach (var environment in await client.GetEnvironmentsAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var thermostat in await client.GetThermostatsAsync(environment.Id, cancellationToken).ConfigureAwait(false))
                {
                    var known = previous.FirstOrDefault(x => x.Id == thermostat.Id);
                    thermostat.Schedules = readSchedules || known == null
                        ? await ReadSchedulesAsync(client, thermostat, cancellationToken).ConfigureAwait(false)
                        : known.Schedules;
                    thermostat.Schedule ??= thermostat.Schedules.FirstOrDefault(x => x.Active)?.Name;
                    await ReadEnergyAsync(client, thermostat, known, now, cancellationToken).ConfigureAwait(false);
                    thermostats.Add(thermostat);
                }
            }
            if (readSchedules)
                _schedulesRead = now;
            return thermostats;
        }

        private async Task<List<ThemoSchedule>> ReadSchedulesAsync(ThemoClient client, ThemoThermostat thermostat, CancellationToken cancellationToken)
        {
            try
            {
                return (await client.GetSchedulesAsync(thermostat.EnvironmentId, thermostat.Id, cancellationToken).ConfigureAwait(false)).ToList();
            }
            catch (ThemoRefusedException error)
            {
                Logger.LogDebug("Themo refused the schedules of {Thermostat}: {Reason}", thermostat.Name, error.Message);
                return [];
            }
        }

        // The finished hours of the last two days, and today's sum from local midnight. Without them (the v2.1 data
        // endpoint gone, or a failure) the thermostat keeps the energy it had.
        private async Task ReadEnergyAsync(ThemoClient client, ThemoThermostat thermostat, ThemoThermostat? known, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var hour = new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
            var local = TimeZoneInfo.ConvertTime(now, Time.LocalTimeZone);
            var midnight = new DateTimeOffset(local.Date, Time.LocalTimeZone.GetUtcOffset(local.Date)).ToUniversalTime();
            var first = hour.AddHours(-EnergyHours);
            var from = midnight < first ? midnight : first;

            List<ThemoEnergyHour> hours;
            try
            {
                hours = await client.GetEnergyAsync(thermostat.EnvironmentId, thermostat.Id, from, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ThemoRefusedException or ThemoUnavailableException)
            {
                if (!_energyWarned)
                    Logger.LogWarning("Could not read Themo's hourly data; the energy reports wait: {Reason}", error.Message);
                _energyWarned = true;
                thermostat.Hours = known?.Hours ?? [];
                thermostat.EnergyToday = known?.EnergyToday;
                thermostat.HeatingShareToday = known?.HeatingShareToday;
                return;
            }
            _energyWarned = false;

            var finished = hours.Where(x => x.Start < hour).ToList();
            thermostat.Hours = finished.Where(x => x.Start >= first).ToList();
            var today = finished.Where(x => x.Start >= midnight).Sum(x => x.Energy);
            thermostat.EnergyToday = Math.Round(today, 3);
            var elapsed = (hour - midnight).TotalHours;
            thermostat.HeatingShareToday = elapsed <= 0 ? 0
                : thermostat.MaxPower is double power ? Math.Round(Math.Clamp(today / (power * elapsed) * 100, 0, 100), 1)
                : null;
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            if (string.Equals(command.Address, RefreshAddress, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshThermostatsAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var (thermostatId, key) = SplitAddress(command.Address);
            if (thermostatId == null)
                throw new UnknownCommandException(commandId);

            var client = _client;
            var thermostat = FindThermostat(thermostatId.Value);
            if (thermostat == null && client != null && _lastPoll == null)
            {
                // Themo was unavailable at start: read the thermostats now.
                await PollAsync(cancellationToken).ConfigureAwait(false);
                thermostat = FindThermostat(thermostatId.Value);
            }
            if (client == null || thermostat == null)
                throw new DeviceNotReadyException($"Themo thermostat {thermostatId} isn't loaded yet.");

            if (key == Schedule)
            {
                await ActivateScheduleAsync(client, thermostat, value, cancellationToken).ConfigureAwait(false);
                return;
            }

            var messages = ParseCommand(key, value, thermostat) ?? throw new UnknownCommandException(commandId);
            var sent = new List<ThemoMessage>();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var message in messages)
                {
                    await client.SendCommandAsync(thermostat.EnvironmentId, thermostat.Id, message.Field, message.Value, cancellationToken).ConfigureAwait(false);
                    sent.Add(message);
                }
            }
            catch (ThemoRefusedException error)
            {
                throw new InvalidOperationException($"Themo refused the command: {error.Message}", error);
            }
            finally
            {
                _gate.Release();
                // Themo applies a command with a delay; report what was sent now, the next poll confirms it.
                if (sent.Count > 0)
                    Update(thermostat.Id, x => sent.ForEach(m => Apply(x, m)));
            }
        }

        private async Task ActivateScheduleAsync(ThemoClient client, ThemoThermostat thermostat, string value, CancellationToken cancellationToken)
        {
            var name = CommandText(value).Trim();
            var schedule = thermostat.Schedules.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(thermostat.Schedules.Count == 0
                    ? "The thermostat has no schedules."
                    : $"The schedule must be one of {string.Join(", ", thermostat.Schedules.Select(x => x.Name))}.", nameof(value));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await client.ActivateScheduleAsync(thermostat.EnvironmentId, thermostat.Id, schedule, cancellationToken).ConfigureAwait(false);
            }
            catch (ThemoRefusedException error)
            {
                throw new InvalidOperationException($"Themo refused the schedule change: {error.Message}", error);
            }
            finally
            {
                _gate.Release();
            }

            // Read the schedules again at the next poll.
            _schedulesRead = null;
            Update(thermostat.Id, x =>
            {
                x.Schedule = schedule.Name;
                x.Schedules = x.Schedules.Select(s => s with { Active = s.Id == schedule.Id }).ToList();
            });
        }

        private void Update(int thermostatId, Action<ThemoThermostat> change)
        {
            ThemoThermostat? updated = null;
            lock (_lock)
            {
                _thermostats = _thermostats.Select(x =>
                {
                    if (x.Id != thermostatId)
                        return x;
                    updated = x.Copy();
                    change(updated);
                    return updated;
                }).ToList();
            }
            if (updated != null)
                SendStateReports(updated);
        }

        /// <summary>
        /// The messages a command sends, or null for an unknown key. A value that doesn't fit throws. A target
        /// temperature switches a thermostat that isn't in manual mode to manual first, because only manual mode
        /// follows it.
        /// </summary>
        internal static IReadOnlyList<ThemoMessage>? ParseCommand(string key, string value, ThemoThermostat thermostat)
        {
            switch (key)
            {
                case SetTemperature:
                    var target = TemperatureValue(value, MinTemperature, MaxTemperature, "temperature");
                    return thermostat.Mode == ThemoModes.Manual
                        ? [new("CMT", target)]
                        : [new("CMode", ThemoModes.ToApi(ThemoModes.Manual)), new("CMT", target)];
                case Mode:
                    var mode = ThemoModes.All.FirstOrDefault(x => string.Equals(x, CommandText(value).Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"The mode must be one of {string.Join(", ", ThemoModes.All)}.", nameof(value));
                    return [new("CMode", ThemoModes.ToApi(mode))];
                case ThermostatMode:
                    return [new("CMode", ThemoModes.ToApi(ThemoModes.FromMatter(WholeNumber(value, "thermostat mode"))))];
                case Lights:
                    return [new("CLights", CommandBool(value) ? 1 : 0)];
                case Lock:
                    var text = CommandText(value).Trim();
                    var state = ThemoModes.Locks.ToList().FindIndex(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
                    if (state < 0 && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 && number < ThemoModes.Locks.Count)
                        state = number;
                    if (state < 0)
                        throw new ArgumentException($"The lock must be one of {string.Join(", ", ThemoModes.Locks)}.", nameof(value));
                    if (state == 3)
                        RequireSoftware(thermostat, _boostLockSoftware, "The timer boost lock");
                    return [new("CLock", state)];
                case FrostProtection:
                    return [new("CFTS", CommandBool(value) ? 1 : 0)];
                case FrostTemperature:
                    return [new("CFT", TemperatureValue(value, MinFrostTemperature, MaxFrostTemperature, "frost protection temperature"))];
                case Boost:
                    var minutes = WholeNumber(value, "boost");
                    if (minutes < 0 || minutes > MaxBoostMinutes)
                        throw new ArgumentOutOfRangeException(nameof(value), minutes, $"The boost must be 0-{MaxBoostMinutes} minutes.");
                    RequireSoftware(thermostat, _boostSoftware, "The boost");
                    return [new("CManTimer", minutes)];
                default:
                    return null;
            }
        }

        /// <summary>The state a sent message leads to.</summary>
        internal static void Apply(ThemoThermostat thermostat, ThemoMessage message)
        {
            switch (message.Field)
            {
                case "CMode":
                    thermostat.Mode = ThemoModes.FromApi((string)message.Value);
                    break;
                case "CMT":
                    thermostat.SetTemperature = (double)message.Value;
                    break;
                case "CLights":
                    thermostat.Lights = (int)message.Value != 0;
                    break;
                case "CLock":
                    thermostat.Lock = (int)message.Value;
                    break;
                case "CFTS":
                    thermostat.FrostProtection = (int)message.Value != 0;
                    break;
                case "CFT":
                    thermostat.FrostTemperature = (double)message.Value;
                    break;
            }
        }

        private static void RequireSoftware(ThemoThermostat thermostat, Version minimum, string what)
        {
            if (SoftwareVersion(thermostat.Software) is Version version && version < minimum)
                throw new InvalidOperationException($"{what} needs thermostat software {minimum} or later; the thermostat has {thermostat.Software}.");
        }

        internal static Version? SoftwareVersion(string? software)
        {
            var digits = new string((software ?? "").Trim().TrimStart('v', 'V').TakeWhile(x => char.IsAsciiDigit(x) || x == '.').ToArray()).Trim('.');
            if (digits.Length == 0)
                return null;
            if (!digits.Contains('.'))
                digits += ".0";
            return Version.TryParse(digits, out var version) ? version : null;
        }

        private static double TemperatureValue(string value, double minimum, double maximum, string what)
        {
            var number = Math.Round(Math.Round(CommandNumber(value) * 2, MidpointRounding.AwayFromZero) / 2, 1);
            if (number < minimum || number > maximum)
                throw new ArgumentOutOfRangeException(nameof(value), number, $"The {what} must be {Format(minimum)}-{Format(maximum)} °C.");
            return number;
        }

        private static int WholeNumber(string value, string what)
        {
            var number = CommandNumber(value);
            if (number != Math.Floor(number) || number < int.MinValue || number > int.MaxValue)
                throw new ArgumentException($"The {what} is a whole number.", nameof(value));
            return (int)number;
        }

        // A command value arrives as JSON: 21.5, "manual", true. A bare word is taken as text.
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

        internal static string Address(int thermostatId, string key) => $"{thermostatId.ToString(CultureInfo.InvariantCulture)}|{key}";

        private static (int? ThermostatId, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            if (separator <= 0 || !int.TryParse(address![..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return (null, "");
            return (id, address[(separator + 1)..]);
        }

        private ThemoThermostat? FindThermostat(int thermostatId)
        {
            lock (_lock)
                return _thermostats.FirstOrDefault(x => x.Id == thermostatId);
        }

        private ReportTemplate? FindTemplate(int thermostatId, string key)
        {
            var address = Address(thermostatId, key);
            return ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The state report values by key; a value the thermostat doesn't have is left out.</summary>
        private static IEnumerable<(string Key, object? Value)> Values(ThemoThermostat thermostat)
        {
            yield return (Temperature, thermostat.Temperature);
            yield return (RoomTemperature, thermostat.RoomTemperature);
            yield return (FloorTemperature, thermostat.FloorTemperature);
            yield return (OutsideTemperature, thermostat.OutsideTemperature);
            yield return (SetTemperature, thermostat.SetTemperature);
            yield return (Mode, thermostat.Mode);
            yield return (ThermostatMode, ThemoModes.ToMatter(thermostat.Mode));
            yield return (Heating, thermostat.Heating);
            yield return (Connected, thermostat.Connected);
            yield return (Lights, thermostat.Lights);
            yield return (Lock, ThemoModes.LockName(thermostat.Lock));
            yield return (FrostProtection, thermostat.FrostProtection);
            yield return (FrostTemperature, thermostat.FrostTemperature);
            yield return (Schedule, thermostat.Schedule);
            yield return (EnergyToday, thermostat.EnergyToday);
            yield return (HeatingShareToday, thermostat.HeatingShareToday);
        }

        private void SendStateReports(ThemoThermostat thermostat)
        {
            var now = DateTime.UtcNow.ToEpoch();
            foreach (var (key, value) in Values(thermostat))
            {
                if (value == null || FindTemplate(thermostat.Id, key) is not ReportTemplate template)
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

        // Each finished hour once, time-stamped to the hour's start; after a restart the hours it missed, at most two
        // days. The series of the last two days is sent when it changes.
        private void SendEnergyReports(ThemoThermostat thermostat)
        {
            if (thermostat.Hours.Count == 0)
                return;

            if (FindTemplate(thermostat.Id, EnergyHour) is ReportTemplate hourTemplate)
            {
                var last = LastReportedHour(thermostat.Id);
                List<ThemoEnergyHour> hours = last is DateTimeOffset reported
                    ? thermostat.Hours.Where(x => x.Start > reported).ToList()
                    : [thermostat.Hours[^1]];
                foreach (var hour in hours)
                {
                    SendReport(this, new Report
                    {
                        Id = hourTemplate.Id,
                        TimeStamp = hour.Start.ToUnixTimeSeconds(),
                        Value = new ValueModel(hour.Energy),
                        Filter = ""
                    });
                }
                if (hours.Count > 0)
                    SaveLastReportedHour(thermostat.Id, hours[^1].Start);
            }

            if (FindTemplate(thermostat.Id, EnergyHourly) is ReportTemplate seriesTemplate)
            {
                var key = string.Join(";", thermostat.Hours.Select(x => $"{x.Start.UtcTicks}:{x.Energy.ToString(CultureInfo.InvariantCulture)}"));
                lock (_lock)
                {
                    if (_publishedSeries.TryGetValue(seriesTemplate.Id, out var published) && published == key)
                        return;
                    _publishedSeries[seriesTemplate.Id] = key;
                }
                var series = new ForecastSeries
                {
                    IssuedAt = Time.GetUtcNow(),
                    Kind = ForecastSeries.ConfirmedKind,
                    Unit = "kWh",
                    Resolution = "PT1H",
                    Points = thermostat.Hours.Select(x => new ForecastPoint(x.Start, x.Start.AddHours(1), x.Energy)).ToList()
                };
                SendReport(this, new Report
                {
                    Id = seriesTemplate.Id,
                    TimeStamp = Time.GetUtcNow().ToUnixTimeSeconds(),
                    Value = series.ToValue(),
                    Filter = ""
                });
            }
        }

        internal string EnergyFile(int thermostatId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(thermostatId.ToString(CultureInfo.InvariantCulture)));
            return Path.Combine(DataDirectory, $"themo-{Convert.ToHexStringLower(hash)[..16]}.json");
        }

        private DateTimeOffset? LastReportedHour(int thermostatId)
        {
            lock (_lock)
            {
                if (_lastReportedHours.TryGetValue(thermostatId, out var cached))
                    return cached;
            }
            var file = EnergyFile(thermostatId);
            try
            {
                var state = File.Exists(file) ? JsonSerializer.Deserialize<ThemoEnergyState>(File.ReadAllText(file)) : null;
                if (state == null)
                    return null;
                lock (_lock)
                    _lastReportedHours[thermostatId] = state.LastReportedHour;
                return state.LastReportedHour;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Could not read the Themo energy file {File}; only the latest hour is reported.", file);
                return null;
            }
        }

        private void SaveLastReportedHour(int thermostatId, DateTimeOffset hour)
        {
            lock (_lock)
                _lastReportedHours[thermostatId] = hour;
            var file = EnergyFile(thermostatId);
            try
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new ThemoEnergyState { LastReportedHour = hour }));
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Could not save the Themo energy file {File}.", file);
            }
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Themo",
                ClassFullName = GetType().FullName!,
                // Every hour, nine minutes past, when the previous hour's consumption is in.
                RefreshSchedule = "0 9 * ? * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    ["email"] = "user@example.com",
                    ["password"] = ""
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            if (State != DeviceState.Running)
                return configuration;

            List<ThemoThermostat> thermostats;
            lock (_lock)
                thermostats = _thermostats.ToList();

            var reports = new List<ReportTemplate>();
            var commands = new List<CommandTemplate>();
            foreach (var thermostat in thermostats)
            {
                reports.AddRange(ThermostatReports(thermostat));
                commands.AddRange(ThermostatCommands(thermostat));
            }
            commands.Add(new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh Themo",
                Address = RefreshAddress,
                Type = ValueType.Boolean,
                Model = true
            });
            configuration.ReportTemplates = reports;
            configuration.CommandTemplates = commands;
            return configuration;
        }

        private static IEnumerable<ReportTemplate> ThermostatReports(ThemoThermostat thermostat)
        {
            var celsius = ValueSchema.Number("°C", ValueSemantics.Temperature);
            if (thermostat.Temperature != null)
                yield return Report(thermostat, Temperature, "temperature", ValueType.Number, thermostat.Temperature.Value, celsius, "°C");
            if (thermostat.RoomTemperature != null)
                yield return Report(thermostat, RoomTemperature, "room temperature", ValueType.Number, thermostat.RoomTemperature.Value, celsius, "°C");
            if (thermostat.FloorTemperature != null)
                yield return Report(thermostat, FloorTemperature, "floor temperature", ValueType.Number, thermostat.FloorTemperature.Value, celsius, "°C");
            if (thermostat.OutsideTemperature != null)
                yield return Report(thermostat, OutsideTemperature, "outside temperature", ValueType.Number, thermostat.OutsideTemperature.Value, celsius, "°C");
            if (thermostat.SetTemperature != null)
                yield return Report(thermostat, SetTemperature, "target temperature", ValueType.Number, thermostat.SetTemperature.Value, TargetSchema(), "°C");
            if (thermostat.Mode != null)
            {
                yield return Report(thermostat, Mode, "mode", ValueType.Text, thermostat.Mode, Choices(ThemoModes.All));
                yield return Report(thermostat, ThermostatMode, "thermostat mode", ValueType.Number, ThemoModes.ToMatter(thermostat.Mode)!, MatterModes());
            }
            if (thermostat.Heating != null)
                yield return Report(thermostat, Heating, "heating", ValueType.Boolean, thermostat.Heating.Value, ValueSchema.Boolean());
            if (thermostat.Connected != null)
                yield return Report(thermostat, Connected, "connected", ValueType.Boolean, thermostat.Connected.Value, ValueSchema.Boolean());
            if (thermostat.Lights != null)
                yield return Report(thermostat, Lights, "backlight", ValueType.Boolean, thermostat.Lights.Value, ValueSchema.Boolean(ValueSemantics.Light));
            if (ThemoModes.LockName(thermostat.Lock) is string lockName)
                yield return Report(thermostat, Lock, "child lock", ValueType.Text, lockName, Choices(ThemoModes.Locks));
            if (thermostat.FrostProtection != null)
                yield return Report(thermostat, FrostProtection, "frost protection", ValueType.Boolean, thermostat.FrostProtection.Value, ValueSchema.Boolean());
            if (thermostat.FrostTemperature != null)
                yield return Report(thermostat, FrostTemperature, "frost protection temperature", ValueType.Number, thermostat.FrostTemperature.Value, FrostSchema(), "°C");
            if (thermostat.Schedule != null || thermostat.Schedules.Count > 0)
                yield return Report(thermostat, Schedule, "schedule", ValueType.Text, thermostat.Schedule ?? "", ScheduleChoices(thermostat));
            if (thermostat.Hours.Count > 0)
            {
                var kwh = ValueSchema.Number("kWh", ValueSemantics.Energy, 0);
                yield return Report(thermostat, EnergyHour, "energy per hour", ValueType.Number, thermostat.Hours[^1].Energy, kwh, "kWh");
                yield return Report(thermostat, EnergyHourly, "hourly energy", ValueType.Entity, null,
                    ValueSchema.Forecast(new ValueSchema { Type = "number", Unit = "kWh", Semantic = ValueSemantics.Energy, Title = "Energy" }), "kWh");
            }
            if (thermostat.EnergyToday != null)
                yield return Report(thermostat, EnergyToday, "energy today", ValueType.Number, thermostat.EnergyToday.Value,
                    ValueSchema.Number("kWh", ValueSemantics.Energy, 0), "kWh");
            if (thermostat.HeatingShareToday != null)
                yield return Report(thermostat, HeatingShareToday, "heating share today", ValueType.Number, thermostat.HeatingShareToday.Value,
                    ValueSchema.Number("%", null, 0, 100), "%");
        }

        private static IEnumerable<CommandTemplate> ThermostatCommands(ThemoThermostat thermostat)
        {
            yield return Command(thermostat, SetTemperature, "set target temperature", ValueType.Number, thermostat.SetTemperature ?? 21, TargetSchema());
            yield return Command(thermostat, Mode, "set mode", ValueType.Text, ThemoModes.Manual, Choices(ThemoModes.All));
            yield return Command(thermostat, ThermostatMode, "set thermostat mode", ValueType.Number, ThemoModes.MatterHeat, MatterModes());
            if (thermostat.Schedules.Count > 0)
                yield return Command(thermostat, Schedule, "set schedule", ValueType.Text, thermostat.Schedules[0].Name, ScheduleChoices(thermostat));
            yield return Command(thermostat, Lights, "set backlight", ValueType.Boolean, true, ValueSchema.Boolean(ValueSemantics.Light));
            yield return Command(thermostat, Lock, "set child lock", ValueType.Text, "off", Choices(ThemoModes.Locks));
            yield return Command(thermostat, FrostProtection, "set frost protection", ValueType.Boolean, true, ValueSchema.Boolean());
            yield return Command(thermostat, FrostTemperature, "set frost protection temperature", ValueType.Number, thermostat.FrostTemperature ?? 7, FrostSchema());
            var boost = ValueSchema.Integer("min", null, 0, MaxBoostMinutes);
            boost.Description = "Manual mode timer in minutes";
            yield return Command(thermostat, Boost, "boost", ValueType.Number, 60, boost);
        }

        private static ValueSchema TargetSchema()
        {
            var schema = ValueSchema.Number("°C", ValueSemantics.Temperature, MinTemperature, MaxTemperature);
            schema.MultipleOf = 0.5;
            return schema;
        }

        private static ValueSchema FrostSchema()
        {
            var schema = ValueSchema.Number("°C", ValueSemantics.Temperature, MinFrostTemperature, MaxFrostTemperature);
            schema.MultipleOf = 0.5;
            return schema;
        }

        private static ValueSchema ScheduleChoices(ThemoThermostat thermostat) =>
            Choices(thermostat.Schedules.Select(x => x.Name).Append(thermostat.Schedule ?? "").Where(x => x.Length > 0).Distinct().ToList());

        private static ValueSchema Choices(IReadOnlyList<string> names)
        {
            var schema = ValueSchema.Text();
            schema.Enum = names.Cast<object>().ToList();
            return schema;
        }

        private static ValueSchema MatterModes()
        {
            var schema = ValueSchema.Integer();
            schema.Description = "Matter thermostat SystemMode: 0 off, 1 auto (SLS), 4 heat (manual)";
            schema.Enum = _matterModes.Cast<object>().ToList();
            return schema;
        }

        private static ReportTemplate Report(ThemoThermostat thermostat, string key, string name, ValueType type, object? model, ValueSchema schema, string? unit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(thermostat.Id, key),
                Name = $"{thermostat.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (unit != null)
                template.Parameters["unit"] = unit;
            return template;
        }

        private static CommandTemplate Command(ThemoThermostat thermostat, string key, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = Address(thermostat.Id, key),
            Name = $"{thermostat.Name} {name}",
            Type = type,
            Model = model,
            Schema = schema
        };

        /// <summary>
        /// Declares every thermostat as a Matter thermostat on the RIoT Control Bridge. The local temperature is the
        /// temperature of the sensor the thermostat regulates by; both setpoints follow the target temperature, and a
        /// controller's setpoint change sends it (switching the thermostat to manual). The system mode follows the
        /// mode: off, heat (manual) or auto (SLS).
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration handed in, because <see cref="GetConfigurationTemplate"/>
        /// mints new template ids on every call. The endpoint id comes from the Themo device id, so a thermostat stays
        /// linked to the same Matter endpoint across restarts and re-imports. The bridge's thermostat has no Auto mode:
        /// a thermostat in SLS shows its setpoints but not the mode.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.CommandTemplates == null || configuration.ReportTemplates == null)
                yield break;

            foreach (var setpoint in configuration.CommandTemplates)
            {
                var (thermostatId, key) = SplitAddress(setpoint.Address);
                if (thermostatId is not int id || key != SetTemperature)
                    continue;

                var current = FindReport(configuration, id, Temperature);
                var target = FindReport(configuration, id, SetTemperature);
                var mode = FindReport(configuration, id, ThermostatMode);
                var modeCommand = configuration.CommandTemplates.FirstOrDefault(x => x.Address == Address(id, ThermostatMode));
                if (current == null || target == null)
                    continue;

                var thermostat = FindThermostat(id);
                var endpoint = new MatterEndpointTemplate
                {
                    Id = $"themo:{id.ToString(CultureInfo.InvariantCulture)}",
                    Name = thermostat?.Name ?? setpoint.Name,
                    DeviceType = MatterDeviceType.Thermostat,
                    VendorName = "Themo",
                    ProductName = thermostat?.Model ?? "Smart Thermostat"
                };
                endpoint.Attributes.Add(Attribute(MatterAttribute.LocalTemperature, current.Id, MatterValueScale.CelsiusToHundredths));
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

        private static ReportTemplate? FindReport(DeviceConfiguration configuration, int thermostatId, string key) =>
            configuration.ReportTemplates.FirstOrDefault(x => x.Address == Address(thermostatId, key));

        private static MatterAttributeBinding Attribute(MatterAttribute attribute, string reportTemplateId, MatterValueScale scale) => new()
        {
            Attribute = attribute,
            ReportTemplateId = reportTemplateId,
            Scale = scale
        };
    }
}
