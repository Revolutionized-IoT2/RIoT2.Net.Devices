using System.Globalization;
using System.Net;
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
using RIoT2.Net.Devices.Services.MyUplink;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="MyUplink"/> device's parameters: the myUplink app and the virtual setpoint.</summary>
    public sealed class MyUplinkOptions
    {
        [DeviceParameter("clientId", Required = true, Description = "Client id of the app registered at dev.myuplink.com")]
        public string ClientId { get; set; } = "";

        [DeviceParameter("clientSecret", Required = true, Secret = true, Description = "Client secret of the app registered at dev.myuplink.com")]
        public string ClientSecret { get; set; } = "";

        [DeviceParameter("language", Default = "en-GB", Description = "Language of the parameter names and alarm texts, e.g. en-GB, fi-FI or sv-SE")]
        public string Language { get; set; } = "en-GB";

        [DeviceParameter("extraParameters", Description = "Comma-separated myUplink parameter ids to report too; writable ones also get a command")]
        public string ExtraParameters { get; set; } = "";

        [DeviceParameter("offsetParameter", Default = "47011", Description = "Parameter id of the heating curve offset (47011: climate system 1)")]
        public string OffsetParameter { get; set; } = "47011";

        [DeviceParameter("baseTemperature", Default = "20", Description = "Room temperature (°C) that heating offset 0 stands for, for the virtual setpoint")]
        public double BaseTemperature { get; set; } = 20;

        [DeviceParameter("degreesPerStep", Default = "1", Description = "Room temperature change (°C) of one heating offset step, for the virtual setpoint")]
        public double DegreesPerStep { get; set; } = 1;
    }

    /// <summary>
    /// NIBE heat pumps (and other myUplink devices) through the official myUplink API. Reports a curated set of
    /// parameters (temperatures, degree minutes, compressor, ventilation, hot water, produced heat), alarms and the
    /// smart home mode; commands set the heating offset, a virtual °C setpoint, hot water, ventilation, the smart home
    /// mode and any writable parameter. Each heat pump is a Matter thermostat.
    /// </summary>
    [RequiresHardware("myUplink API")]
    public class MyUplink : AsyncDeviceBase<MyUplinkOptions>, IDeviceWithConfiguration, IAsyncCommandDevice, IMatterDevice
    {
        // Report and command keys besides the curated ones; an address is "<device id>|<key>".
        internal const string SetTemperature = "setTemperature";
        internal const string ThermostatMode = "thermostatMode";
        internal const string SmartHomeMode = "smartHomeMode";
        internal const string Connected = "connected";
        internal const string Alarm = "alarm";
        internal const string ActiveAlarms = "activeAlarms";
        internal const string AlarmText = "alarmText";
        internal const string PremiumManage = "premiumManage";
        internal const string EnergyToday = "energyToday";
        internal const string SetParameter = "setParameter";
        internal const string RefreshAddress = "refresh";
        // An extra parameter's key is "p<parameter id>".
        internal const string ExtraPrefix = "p";

        /// <summary>The Matter SystemMode of a heat pump that only heats.</summary>
        internal const int MatterHeat = 4;

        internal static readonly IReadOnlyList<string> SmartHomeModes = ["Default", "Normal", "Away", "Vacation", "Home"];

        /// <summary>The shortest time between two polls.</summary>
        internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

        /// <summary>How often the Premium subscription is checked.</summary>
        internal static readonly TimeSpan SubscriptionCheckInterval = TimeSpan.FromDays(1);

        // One myUplink call sequence at a time: a poll and a command don't interleave, and the client isn't thread-safe.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _pumpsLock = new();
        private readonly Dictionary<string, MyUplinkEnergyBaseline> _baselines = [];
        private MyUplinkClient? _client;
        // The heat pumps the last poll read, which the template lists; the template is built without I/O.
        private List<MyUplinkPump> _pumps = [];
        private DateTimeOffset? _lastPoll;
        private DateTimeOffset? _subscriptionsChecked;
        private string _offsetParameter = "47011";
        private List<string> _extraParameters = [];

        public MyUplink(ILogger logger) : base(logger) { }

        /// <summary>The folder of the energy baseline files; tests replace it.</summary>
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
            _subscriptionsChecked = null;
            lock (_pumpsLock)
            {
                _pumps = [];
                _baselines.Clear();
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _offsetParameter = Options.OffsetParameter?.Trim() ?? "";
            if (!IsParameterId(_offsetParameter))
                throw new InvalidOperationException("The offset parameter must be a myUplink parameter id, e.g. 47011.");
            _extraParameters = (Options.ExtraParameters ?? "")
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().ToList();
            var invalid = _extraParameters.Where(x => !IsParameterId(x)).ToList();
            if (invalid.Count > 0)
                throw new InvalidOperationException($"The extra parameters must be myUplink parameter ids: {string.Join(", ", invalid)} isn't.");
            if (!(Options.DegreesPerStep > 0))
                throw new InvalidOperationException("The degrees per step must be more than 0.");

            var language = string.IsNullOrWhiteSpace(Options.Language) ? "en-GB" : Options.Language.Trim();
            _client = new MyUplinkClient(Options.ClientId.Trim(), Options.ClientSecret.Trim(), language, SendAsync, Logger, Time);

            // The first poll's errors, a wrong client secret too, put the device into the error state with the reason.
            // A configured device outlives a myUplink outage at start: the next refresh or command polls again.
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MyUplinkUnavailableException error) when (ReportTemplates?.Any() == true || CommandTemplates?.Any() == true)
            {
                Logger.LogWarning("myUplink is unavailable; the next refresh tries again: {Reason}", error.Message);
            }
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _client = null;
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshPumpsAsync(cancellationToken);

        private async Task RefreshPumpsAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumPollInterval)
            {
                Logger.LogDebug("myUplink was polled less than a minute ago; the refresh is skipped.");
                return;
            }

            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MyUplinkUnavailableException error)
            {
                Logger.LogWarning("Could not refresh myUplink: {Reason}", error.Message);
            }
        }

        private async Task PollAsync(CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The myUplink device isn't started.");
            List<MyUplinkPump> pumps;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pumps = await ReadPumpsAsync(client, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            _lastPoll = Time.GetUtcNow();
            lock (_pumpsLock)
                _pumps = pumps;

            if (pumps.Count == 0)
                Logger.LogWarning("The myUplink app's account has no devices.");
            foreach (var pump in pumps)
                SendPumpReports(pump);
        }

        private async Task<List<MyUplinkPump>> ReadPumpsAsync(MyUplinkClient client, CancellationToken cancellationToken)
        {
            var systems = await client.GetSystemsAsync(cancellationToken).ConfigureAwait(false);
            List<MyUplinkPump> previous;
            lock (_pumpsLock)
                previous = _pumps.ToList();

            var now = Time.GetUtcNow();
            var checkSubscriptions = _subscriptionsChecked is not DateTimeOffset checkedAt || now - checkedAt >= SubscriptionCheckInterval;
            var pumps = new List<MyUplinkPump>();
            foreach (var system in systems)
            {
                var mode = await client.GetSmartHomeModeAsync(system.Id, cancellationToken).ConfigureAwait(false);
                var alarms = await client.GetActiveAlarmsAsync(system.Id, cancellationToken).ConfigureAwait(false);
                var manage = checkSubscriptions ? await client.GetPremiumManageAsync(system.Id, cancellationToken).ConfigureAwait(false) : null;
                if (checkSubscriptions && manage == false)
                    Logger.LogWarning("myUplink system {System} has no Premium Manage subscription: myUplink may refuse changes.", system.Name);

                foreach (var device in system.Devices)
                {
                    var known = previous.FirstOrDefault(x => x.Id == device.Id)?.Copy();
                    var pump = known ?? await DiscoverAsync(client, device, cancellationToken).ConfigureAwait(false);
                    pump.SystemId = system.Id;
                    pump.ProductName = device.ProductName;
                    pump.Name = system.Devices.Count == 1 || string.IsNullOrEmpty(device.ProductName)
                        ? FirstText(system.Name, device.ProductName, device.Id)
                        : $"{FirstText(system.Name, device.Id)} {device.ProductName}";
                    pump.Connected = device.Connected;
                    pump.HasAlarm = system.HasAlarm;
                    pump.SmartHomeMode = mode ?? pump.SmartHomeMode;
                    if (alarms != null)
                    {
                        pump.ActiveAlarms = alarms.Count;
                        pump.AlarmText = alarms.Count == 0 ? "" : alarms.Latest ?? "";
                    }
                    pump.PremiumManage = manage ?? pump.PremiumManage;

                    // A newly discovered pump read all its values already.
                    var ids = pump.PolledIds.ToList();
                    if (known != null && ids.Count > 0)
                    {
                        foreach (var point in await client.GetPointsAsync(device.Id, ids, cancellationToken).ConfigureAwait(false))
                        {
                            if (pump.Points.TryGetValue(point.Id, out var cached))
                            {
                                cached.Value = point.Value;
                                cached.Text = point.Text;
                            }
                            else
                            {
                                pump.Points[point.Id] = point;
                            }
                        }
                    }
                    UpdateEnergy(pump);
                    pumps.Add(pump);
                }
            }
            if (checkSubscriptions)
                _subscriptionsChecked = now;
            return pumps;
        }

        private async Task<MyUplinkPump> DiscoverAsync(MyUplinkClient client, MyUplinkSystemDevice device, CancellationToken cancellationToken)
        {
            var points = await client.GetPointsAsync(device.Id, null, cancellationToken).ConfigureAwait(false);
            var pump = new MyUplinkPump
            {
                Id = device.Id,
                Points = points.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First())
            };
            pump.Known = MyUplinkCatalog.Match(pump.Points, _offsetParameter);
            pump.Extras = _extraParameters.Where(pump.Points.ContainsKey).ToList();

            Logger.LogInformation("myUplink device {Product} has {Count} parameters, {Known} of the {Curated} curated ones.",
                device.ProductName, pump.Points.Count, pump.Known.Count, MyUplinkCatalog.Points.Count);
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var point in pump.Points.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
                    Logger.LogDebug("myUplink parameter {Id} {Name} = {Value} {Unit}{Writable}", point.Id, point.Name,
                        point.EnumText ?? point.Text ?? point.Value?.ToString(CultureInfo.InvariantCulture), point.Unit, point.Writable ? " (writable)" : "");
                var missing = MyUplinkCatalog.Points.Select(x => x.Key).Where(x => !pump.Known.ContainsKey(x)).ToList();
                if (missing.Count > 0)
                    Logger.LogDebug("myUplink device {Product} has no parameter for {Keys}.", device.ProductName, string.Join(", ", missing));
            }
            foreach (var id in _extraParameters.Where(x => !pump.Points.ContainsKey(x)))
                Logger.LogWarning("myUplink device {Product} has no parameter {Id}.", device.ProductName, id);
            return pump;
        }

        // The produced heat since the day's first poll; the baseline is kept in a file, so a restart keeps the day.
        private void UpdateEnergy(MyUplinkPump pump)
        {
            var counters = new[] { pump.Point(MyUplinkCatalog.EnergyHeating), pump.Point(MyUplinkCatalog.EnergyHotWater) }
                .Where(x => x?.Value != null).ToList();
            if (counters.Count == 0)
            {
                pump.EnergyToday = null;
                return;
            }

            var total = counters.Sum(x => x!.Value!.Value);
            var day = TimeZoneInfo.ConvertTime(Time.GetUtcNow(), Time.LocalTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var baseline = Baseline(pump.Id);
            // A new day, or a counter that went back (a reset), starts counting again.
            if (baseline == null || baseline.Day != day || total < baseline.Total)
            {
                baseline = new MyUplinkEnergyBaseline { Day = day, Total = total };
                SaveBaseline(pump.Id, baseline);
            }
            pump.EnergyToday = Math.Round(total - baseline.Total, 3);
        }

        internal string BaselineFile(string deviceId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
            return Path.Combine(DataDirectory, $"myuplink-{Convert.ToHexStringLower(hash)[..16]}.json");
        }

        private MyUplinkEnergyBaseline? Baseline(string deviceId)
        {
            lock (_pumpsLock)
            {
                if (_baselines.TryGetValue(deviceId, out var cached))
                    return cached;
            }
            var file = BaselineFile(deviceId);
            try
            {
                var loaded = File.Exists(file) ? JsonSerializer.Deserialize<MyUplinkEnergyBaseline>(File.ReadAllText(file)) : null;
                if (loaded != null)
                {
                    lock (_pumpsLock)
                        _baselines[deviceId] = loaded;
                }
                return loaded;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Could not read the myUplink energy file {File}; today's energy counts from now.", file);
                return null;
            }
        }

        private void SaveBaseline(string deviceId, MyUplinkEnergyBaseline baseline)
        {
            lock (_pumpsLock)
                _baselines[deviceId] = baseline;
            var file = BaselineFile(deviceId);
            try
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(baseline));
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Could not save the myUplink energy file {File}.", file);
            }
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            if (string.Equals(command.Address, RefreshAddress, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshPumpsAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var (pumpId, key) = SplitAddress(command.Address);
            if (pumpId == null)
                throw new UnknownCommandException(commandId);

            var client = _client;
            var pump = FindPump(pumpId);
            if (pump == null && client != null && _lastPoll == null)
            {
                // myUplink was unavailable at start: read the heat pumps now.
                await PollAsync(cancellationToken).ConfigureAwait(false);
                pump = FindPump(pumpId);
            }
            if (client == null || pump == null)
                throw new DeviceNotReadyException($"myUplink device {pumpId} isn't loaded yet.");

            if (key == SmartHomeMode)
            {
                var mode = SmartHomeModes.FirstOrDefault(x => string.Equals(x, CommandText(value).Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"The smart home mode must be one of {string.Join(", ", SmartHomeModes)}.", nameof(value));
                await SendChangeAsync(pump, () => client.SetSmartHomeModeAsync(pump.SystemId, mode, cancellationToken), cancellationToken).ConfigureAwait(false);
                Update(x => x.SystemId == pump.SystemId, x => x.SmartHomeMode = mode);
                return;
            }

            var (parameterId, written) = ParseCommand(key, value, pump, Options.BaseTemperature, Options.DegreesPerStep)
                ?? throw new UnknownCommandException(commandId);
            var values = new Dictionary<string, string> { [parameterId] = written };
            await SendChangeAsync(pump, () => client.SetPointsAsync(pump.Id, values, cancellationToken), cancellationToken).ConfigureAwait(false);

            // The heat pump takes the change from myUplink in a moment; report it now, the next poll confirms it.
            var number = double.Parse(written, NumberStyles.Float, CultureInfo.InvariantCulture);
            Update(x => x.Id == pump.Id, x =>
            {
                if (x.Points.TryGetValue(parameterId, out var point))
                    point.Value = number;
            });
        }

        private async Task SendChangeAsync(MyUplinkPump pump, Func<Task> change, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await change().ConfigureAwait(false);
            }
            catch (MyUplinkRefusedException error) when (error.Status == HttpStatusCode.Forbidden)
            {
                var manage = pump.PremiumManage switch { true => "yes", false => "no", _ => "unknown" };
                throw new InvalidOperationException(
                    $"myUplink refused the change. Changing settings through the API needs a myUplink Premium Manage subscription (Manage active: {manage}).", error);
            }
            catch (MyUplinkRefusedException error)
            {
                throw new InvalidOperationException($"myUplink refused the change: {error.Message}", error);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void Update(Func<MyUplinkPump, bool> which, Action<MyUplinkPump> change)
        {
            List<MyUplinkPump> updated;
            lock (_pumpsLock)
            {
                _pumps = _pumps.Select(x =>
                {
                    if (!which(x))
                        return x;
                    var copy = x.Copy();
                    change(copy);
                    return copy;
                }).ToList();
                updated = _pumps.Where(which).ToList();
            }
            foreach (var pump in updated)
                SendPumpReports(pump);
        }

        /// <summary>
        /// The parameter and value a command writes, or null for an unknown key. A value that doesn't fit throws. The
        /// curated settings are offered whatever myUplink's writable flag says, which may depend on the subscription;
        /// <c>setParameter</c> and the extra parameters only write parameters myUplink marks writable.
        /// </summary>
        internal static (string ParameterId, string Value)? ParseCommand(string key, string value, MyUplinkPump pump, double baseTemperature, double degreesPerStep)
        {
            switch (key)
            {
                case SetTemperature:
                    var offset = pump.Point(MyUplinkCatalog.HeatingOffset)
                        ?? throw new DeviceNotReadyException("The heat pump has no heating offset parameter; check offsetParameter.");
                    var target = CommandNumber(value);
                    var steps = MyUplinkSetpoint.ToOffset(target, baseTemperature, degreesPerStep, offset.Step ?? 1);
                    if (steps < (offset.Minimum ?? double.MinValue) || steps > (offset.Maximum ?? double.MaxValue))
                    {
                        var low = MyUplinkSetpoint.ToTemperature(offset.Minimum ?? steps, baseTemperature, degreesPerStep);
                        var high = MyUplinkSetpoint.ToTemperature(offset.Maximum ?? steps, baseTemperature, degreesPerStep);
                        throw new ArgumentOutOfRangeException(nameof(value), target,
                            $"The temperature must be {MyUplinkSetpoint.Format(low)}-{MyUplinkSetpoint.Format(high)} °C.");
                    }
                    return (offset.Id, MyUplinkSetpoint.Format(steps));
                case SetParameter:
                    var json = CommandJson(value);
                    if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("parameterId", out var idJson) || !json.TryGetProperty("value", out var valueJson))
                        throw new ArgumentException("The value must be {\"parameterId\": \"<id>\", \"value\": <value>}.", nameof(value));
                    var id = idJson.ValueKind == JsonValueKind.Number ? idJson.GetRawText() : idJson.GetString() ?? "";
                    if (!pump.Points.TryGetValue(id.Trim(), out var parameter))
                        throw new ArgumentException($"The heat pump has no parameter {id}.", nameof(value));
                    if (!parameter.Writable)
                        throw new ArgumentException($"Parameter {parameter.Id} ({parameter.Name}) is read-only.", nameof(value));
                    return (parameter.Id, PointValue(parameter, valueJson.GetRawText()));
                case MyUplinkCatalog.HeatingOffset:
                case MyUplinkCatalog.HotWaterComfortMode:
                case MyUplinkCatalog.HotWaterBoost:
                case MyUplinkCatalog.VentilationSpeed:
                    var setting = pump.Point(key);
                    return setting == null ? null : (setting.Id, PointValue(setting, value));
                default:
                    if (key.StartsWith(ExtraPrefix, StringComparison.Ordinal) && pump.Extras.Contains(key[ExtraPrefix.Length..]) &&
                        pump.Points.TryGetValue(key[ExtraPrefix.Length..], out var extra) && extra.Writable)
                        return (extra.Id, PointValue(extra, value));
                    return null;
            }
        }

        /// <summary>The value to write to <paramref name="point"/>: an enum's value, or a number rounded to its step and in its range.</summary>
        internal static string PointValue(MyUplinkPoint point, string value)
        {
            if (point.Enum.Count > 0)
            {
                var text = CommandText(value).Trim();
                var number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? MyUplinkSetpoint.Format(n) : null;
                var match = point.Enum.FirstOrDefault(x => string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase))
                    ?? point.Enum.FirstOrDefault(x => x.Value == text || x.Value == number)
                    ?? throw new ArgumentException($"The {point.Name} must be one of {string.Join(", ", point.Enum.Select(x => x.Text))}.", nameof(value));
                return match.Value;
            }

            var result = CommandNumber(value);
            if (point.Step is double step)
                result = Math.Round(Math.Round(result / step, MidpointRounding.AwayFromZero) * step, 6);
            if (result < (point.Minimum ?? double.MinValue) || result > (point.Maximum ?? double.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(value), result,
                    $"The {point.Name} must be {MyUplinkSetpoint.Format(point.Minimum ?? result)}-{MyUplinkSetpoint.Format(point.Maximum ?? result)}.");
            return MyUplinkSetpoint.Format(result);
        }

        // A command value arrives as JSON: 22, "Away", {"parameterId": ...}. A bare word is taken as text.
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

        private static bool IsParameterId(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

        private static string FirstText(params string?[] texts) => texts.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

        internal static string Address(string pumpId, string key) => $"{pumpId}|{key}";

        private static (string? PumpId, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            return separator <= 0 ? (null, "") : (address![..separator], address[(separator + 1)..]);
        }

        private MyUplinkPump? FindPump(string pumpId)
        {
            lock (_pumpsLock)
                return _pumps.FirstOrDefault(x => x.Id == pumpId);
        }

        /// <summary>The report values of <paramref name="pump"/> by key; a value the pump doesn't have is left out.</summary>
        private IEnumerable<(string Key, object Value)> Values(MyUplinkPump pump)
        {
            foreach (var entry in MyUplinkCatalog.Points)
            {
                if (pump.Point(entry.Key) is MyUplinkPoint point && ReportValue(point) is object value)
                    yield return (entry.Key, value);
            }
            if (pump.Point(MyUplinkCatalog.HeatingOffset)?.Value is double offset)
            {
                yield return (SetTemperature, MyUplinkSetpoint.ToTemperature(offset, Options.BaseTemperature, Options.DegreesPerStep));
                yield return (ThermostatMode, MatterHeat);
            }
            if (pump.SmartHomeMode != null)
                yield return (SmartHomeMode, pump.SmartHomeMode);
            if (pump.Connected is bool connected)
                yield return (Connected, connected);
            if (pump.HasAlarm is bool alarm)
                yield return (Alarm, alarm);
            if (pump.ActiveAlarms is int count)
                yield return (ActiveAlarms, count);
            if (pump.AlarmText != null)
                yield return (AlarmText, pump.AlarmText);
            if (pump.PremiumManage is bool manage)
                yield return (PremiumManage, manage);
            if (pump.EnergyToday is double energy)
                yield return (EnergyToday, energy);
            foreach (var id in pump.Extras)
            {
                if (pump.Points.TryGetValue(id, out var point) && ReportValue(point) is object value)
                    yield return (ExtraPrefix + id, value);
            }
        }

        // An enum parameter reports its text, so "Economy" rather than 0.
        private static object? ReportValue(MyUplinkPoint point)
        {
            if (point.Enum.Count > 0)
                return point.EnumText ?? (point.Value is double raw ? MyUplinkSetpoint.Format(raw) : point.Text);
            return point.Value;
        }

        private void SendPumpReports(MyUplinkPump pump)
        {
            var now = DateTime.UtcNow.ToEpoch();
            foreach (var (key, value) in Values(pump))
            {
                var address = Address(pump.Id, key);
                var template = ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));
                if (template == null)
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

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "myUplink",
                ClassFullName = GetType().FullName!,
                // Every hour, seven minutes past; the heat pump uploads its values every few minutes anyway.
                RefreshSchedule = "0 7 * ? * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    ["clientId"] = "",
                    ["clientSecret"] = "",
                    ["language"] = "en-GB",
                    ["extraParameters"] = "",
                    ["offsetParameter"] = "47011",
                    ["baseTemperature"] = "20",
                    ["degreesPerStep"] = "1"
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            if (State != DeviceState.Running)
                return configuration;

            List<MyUplinkPump> pumps;
            lock (_pumpsLock)
                pumps = _pumps.ToList();

            var reports = new List<ReportTemplate>();
            var commands = new List<CommandTemplate>();
            foreach (var pump in pumps)
            {
                reports.AddRange(PumpReports(pump));
                commands.AddRange(PumpCommands(pump));
            }
            commands.Add(new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh myUplink",
                Address = RefreshAddress,
                Type = ValueType.Boolean,
                Model = true
            });
            configuration.ReportTemplates = reports;
            configuration.CommandTemplates = commands;
            return configuration;
        }

        private IEnumerable<ReportTemplate> PumpReports(MyUplinkPump pump)
        {
            foreach (var entry in MyUplinkCatalog.Points)
            {
                if (pump.Point(entry.Key) is MyUplinkPoint point)
                    yield return PointReport(pump, entry.Key, entry.Name, point, entry.Unit, entry.Semantic);
            }
            if (pump.Point(MyUplinkCatalog.HeatingOffset) is MyUplinkPoint offset)
            {
                yield return Report(pump, SetTemperature, "target temperature (virtual)", ValueType.Number,
                    MyUplinkSetpoint.ToTemperature(offset.Value ?? 0, Options.BaseTemperature, Options.DegreesPerStep), SetpointSchema(offset), "°C");
                var mode = ValueSchema.Integer();
                mode.Description = "Matter thermostat SystemMode: 4 heat";
                mode.Enum = [MatterHeat];
                mode.ReadOnly = true;
                yield return Report(pump, ThermostatMode, "thermostat mode", ValueType.Number, MatterHeat, mode);
            }
            if (pump.SmartHomeMode != null)
                yield return Report(pump, SmartHomeMode, "smart home mode", ValueType.Text, pump.SmartHomeMode, Choices(SmartHomeModes));
            if (pump.Connected != null)
                yield return Report(pump, Connected, "connected", ValueType.Boolean, pump.Connected.Value, ValueSchema.Boolean());
            if (pump.HasAlarm != null)
                yield return Report(pump, Alarm, "alarm", ValueType.Boolean, pump.HasAlarm.Value, ValueSchema.Boolean());
            if (pump.ActiveAlarms != null)
            {
                yield return Report(pump, ActiveAlarms, "active alarms", ValueType.Number, pump.ActiveAlarms.Value, ValueSchema.Integer(minimum: 0));
                yield return Report(pump, AlarmText, "latest alarm", ValueType.Text, pump.AlarmText ?? "", ValueSchema.Text(ValueSemantics.Text));
            }
            if (pump.PremiumManage != null)
                yield return Report(pump, PremiumManage, "Premium Manage", ValueType.Boolean, pump.PremiumManage.Value, ValueSchema.Boolean());
            if (pump.EnergyToday != null)
                yield return Report(pump, EnergyToday, "heat produced today", ValueType.Number, pump.EnergyToday.Value,
                    ValueSchema.Number("kWh", ValueSemantics.Energy, 0), "kWh");
            foreach (var id in pump.Extras)
            {
                if (pump.Points.TryGetValue(id, out var point))
                    yield return PointReport(pump, ExtraPrefix + id, FirstText(point.Name, id), point, null, null);
            }
        }

        private IEnumerable<CommandTemplate> PumpCommands(MyUplinkPump pump)
        {
            if (pump.Point(MyUplinkCatalog.HeatingOffset) is MyUplinkPoint offset)
                yield return Command(pump, SetTemperature, "set target temperature (virtual)", ValueType.Number,
                    MyUplinkSetpoint.ToTemperature(offset.Value ?? 0, Options.BaseTemperature, Options.DegreesPerStep), SetpointSchema(offset));
            foreach (var entry in MyUplinkCatalog.Points.Where(x => x.Command))
            {
                if (pump.Point(entry.Key) is MyUplinkPoint point)
                    yield return PointCommand(pump, entry.Key, "set " + entry.Name, point);
            }
            if (pump.SmartHomeMode != null)
                yield return Command(pump, SmartHomeMode, "set smart home mode", ValueType.Text, "Away", Choices(SmartHomeModes));
            foreach (var id in pump.Extras)
            {
                if (pump.Points.TryGetValue(id, out var point) && point.Writable)
                    yield return PointCommand(pump, ExtraPrefix + id, "set " + FirstText(point.Name, id), point);
            }
            var parameter = ValueSchema.Entity(new Dictionary<string, ValueSchema>
            {
                ["parameterId"] = ValueSchema.Text(),
                ["value"] = ValueSchema.Text()
            });
            parameter.Description = "Writes any writable myUplink parameter: {\"parameterId\": \"47011\", \"value\": \"1\"}";
            yield return Command(pump, SetParameter, "set parameter", ValueType.Entity,
                new Dictionary<string, string> { ["parameterId"] = _offsetParameter, ["value"] = "0" }, parameter);
        }

        private ValueSchema SetpointSchema(MyUplinkPoint offset)
        {
            var schema = ValueSchema.Number("°C", ValueSemantics.Temperature,
                offset.Minimum is double min ? MyUplinkSetpoint.ToTemperature(min, Options.BaseTemperature, Options.DegreesPerStep) : null,
                offset.Maximum is double max ? MyUplinkSetpoint.ToTemperature(max, Options.BaseTemperature, Options.DegreesPerStep) : null);
            schema.Description = "Converted to the heating offset: (target - baseTemperature) / degreesPerStep";
            return schema;
        }

        private static ReportTemplate PointReport(MyUplinkPump pump, string key, string name, MyUplinkPoint point, string? unit, string? semantic)
        {
            var (type, model, schema) = PointShape(point, unit, semantic);
            return Report(pump, key, name, type, model, schema, schema.Unit);
        }

        private static CommandTemplate PointCommand(MyUplinkPump pump, string key, string name, MyUplinkPoint point)
        {
            var (type, model, schema) = PointShape(point, null, null);
            return Command(pump, key, name, type, model, schema);
        }

        // An enum parameter is text with its choices; any other a number with myUplink's unit, range and step.
        private static (ValueType Type, object Model, ValueSchema Schema) PointShape(MyUplinkPoint point, string? unit, string? semantic)
        {
            if (point.Enum.Count > 0)
                return (ValueType.Text, ReportValue(point) ?? point.Enum[0].Text, Choices(point.Enum.Select(x => x.Text).ToList()));
            var schema = ValueSchema.Number(FirstText(point.Unit, unit) is { Length: > 0 } u ? u : null, semantic, point.Minimum, point.Maximum);
            schema.MultipleOf = point.Step;
            return (ValueType.Number, point.Value ?? 0, schema);
        }

        private static ReportTemplate Report(MyUplinkPump pump, string key, string name, ValueType type, object model, ValueSchema schema, string? valueUnit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(pump.Id, key),
                Name = $"{pump.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (!string.IsNullOrEmpty(valueUnit))
                template.Parameters["unit"] = valueUnit;
            return template;
        }

        private static CommandTemplate Command(MyUplinkPump pump, string key, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = Address(pump.Id, key),
            Name = $"{pump.Name} {name}",
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

        /// <summary>
        /// Declares every heat pump with a heating offset and an exhaust air sensor as a Matter thermostat on the RIoT
        /// Control Bridge. Without a room sensor, the local temperature is the exhaust air temperature (BT20), which
        /// follows the room temperature with some delay; the setpoints are the virtual setpoint, and a controller's
        /// setpoint change sends the set temperature command. The system mode is always heat.
        /// </summary>
        /// <remarks>
        /// The bindings are read against the configuration handed in, because <see cref="GetConfigurationTemplate"/>
        /// mints new template ids on every call. The endpoint id comes from the myUplink device id, so a heat pump stays
        /// linked to the same Matter endpoint across restarts and re-imports.
        /// </remarks>
        public IEnumerable<MatterEndpointTemplate> GetMatterEndpoints(DeviceConfiguration configuration)
        {
            if (configuration?.CommandTemplates == null || configuration.ReportTemplates == null)
                yield break;

            foreach (var setpoint in configuration.CommandTemplates)
            {
                var (pumpId, key) = SplitAddress(setpoint.Address);
                if (pumpId == null || key != SetTemperature)
                    continue;

                var room = FindReport(configuration, pumpId, MyUplinkCatalog.ExhaustAirTemperature);
                var target = FindReport(configuration, pumpId, SetTemperature);
                var mode = FindReport(configuration, pumpId, ThermostatMode);
                if (room == null || target == null)
                    continue;

                var pump = FindPump(pumpId);
                var endpoint = new MatterEndpointTemplate
                {
                    Id = $"myuplink:{pumpId}",
                    Name = pump?.Name ?? setpoint.Name,
                    DeviceType = MatterDeviceType.Thermostat,
                    VendorName = "NIBE",
                    ProductName = string.IsNullOrEmpty(pump?.ProductName) ? "myUplink heat pump" : pump.ProductName
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
                yield return endpoint;
            }
        }

        private static ReportTemplate? FindReport(DeviceConfiguration configuration, string pumpId, string key) =>
            configuration.ReportTemplates.FirstOrDefault(x => x.Address == Address(pumpId, key));

        private static MatterAttributeBinding Attribute(MatterAttribute attribute, string reportTemplateId, MatterValueScale scale) => new()
        {
            Attribute = attribute,
            ReportTemplateId = reportTemplateId,
            Scale = scale
        };
    }
}
