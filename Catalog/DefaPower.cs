using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.DefaPower;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="DefaPower"/> device's parameters: the phone number the DEFA Power app logs in with.</summary>
    public sealed class DefaPowerOptions
    {
        [DeviceParameter("phoneNumber", Required = true,
            Description = "Phone number of the DEFA Power account in international format, e.g. +358401234567; the login code comes to it by SMS")]
        public string PhoneNumber { get; set; } = "";
    }

    /// <summary>
    /// DEFA Power EV chargers through the CloudCharge API that the DEFA Power app uses. The device logs in with an SMS
    /// code (the <c>requestSmsCode</c> and <c>login</c> commands) and keeps the login in the Data folder. Per connector it
    /// reports the charger's state, power and meter, works out the energy of every quarter-hour, hour, day and charging
    /// session from the meter, and their cost from the electricity prices it is given (the <c>prices</c> command). Commands
    /// start and stop the charging, set the current limit, eco mode and manual schedules, charge now and restart the charger.
    /// </summary>
    [RequiresHardware("CloudCharge API")]
    public class DefaPower : AsyncDeviceBase<DefaPowerOptions>, IDeviceWithConfiguration, IAsyncCommandDevice
    {
        // Device report and commands.
        internal const string LoginState = "loginState";
        internal const string RequestSmsCode = "requestSmsCode";
        internal const string Login = "login";
        internal const string RefreshAddress = "refresh";
        internal const string Prices = "prices";

        // Connector report and command keys; an address is "<connector id>|<key>".
        internal const string Status = "status";
        internal const string ChargingState = "chargingState";
        internal const string Charging = "charging";
        internal const string Power = "power";
        internal const string CurrentLimit = "currentLimit";
        internal const string Online = "online";
        internal const string ErrorCode = "errorCode";
        internal const string Meter = "meter";
        internal const string SessionEnergy = "sessionEnergy";
        internal const string EnergyQuarter = "energyQuarter";
        internal const string EnergyHour = "energyHour";
        internal const string EnergyToday = "energyToday";
        internal const string EnergyQuarterly = "energyQuarterly";
        internal const string Session = "session";
        internal const string CostQuarter = "costQuarter";
        internal const string CostToday = "costToday";
        internal const string EcoMode = "ecoMode";
        internal const string EcoHoursToCharge = "ecoHoursToCharge";
        internal const string ManualSchedules = "manualSchedules";
        internal const string PausedBy = "pausedBy";
        internal const string PausedUntil = "pausedUntil";
        internal const string ScheduleOverridden = "scheduleOverridden";
        internal const string ChargeNow = "chargeNow";
        internal const string Restart = "restart";

        internal const string LoggedIn = "loggedIn";
        internal const string LoginRequired = "loginRequired";
        internal const string CodeSent = "codeSent";
        private static readonly string[] _loginStates = [LoggedIn, LoginRequired, CodeSent];
        private static readonly string[] _restartTypes = ["soft", "hard"];

        internal const int MinHoursToCharge = 1;
        internal const int MaxHoursToCharge = 23;
        internal const int MinCurrent = 6;
        internal const int MaxCurrent = 32;

        /// <summary>How often the charger is read while it charges.</summary>
        internal static readonly TimeSpan ChargingInterval = TimeSpan.FromSeconds(15);

        /// <summary>How often the charger is read while it doesn't charge.</summary>
        internal static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

        /// <summary>How often the chargers and their settings are read again.</summary>
        internal static readonly TimeSpan ChargePointInterval = TimeSpan.FromMinutes(15);

        /// <summary>How often eco mode and the schedules are read.</summary>
        internal static readonly TimeSpan SmartChargingInterval = TimeSpan.FromMinutes(5);

        /// <summary>The shortest time between two refresh commands' polls.</summary>
        internal static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(10);

        // How long after a quarter-hour the poll reads the meter, so the quarter's energy is in.
        private static readonly TimeSpan _quarterDelay = TimeSpan.FromSeconds(5);

        // One CloudCharge call sequence at a time: the loop's polls and the commands don't interleave.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _lock = new();
        private readonly Dictionary<string, string> _publishedSeries = [];
        private CloudChargeClient? _client;
        private string _phoneNumber = "";
        // The connectors the last poll read, which the template lists; the template is built without I/O.
        private List<DefaPowerConnector> _connectors = [];
        private DefaPowerState _state = new();
        private bool _stateChanged;
        private string _loginState = LoginRequired;
        private DateTimeOffset? _lastPoll;
        private DateTimeOffset? _chargePointsRead;
        private DateTimeOffset? _smartChargingRead;
        private CancellationTokenSource? _loopStop;
        private Task? _loop;

        public DefaPower(ILogger logger) : base(logger) { }

        /// <summary>The folder of the login and state files; tests replace it.</summary>
        internal string DataDirectory { get; set; } = "Data";

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        /// <summary>Whether the device polls on its own; tests poll with <see cref="PollAsync"/>.</summary>
        internal bool RunLoop { get; set; } = true;

        /// <summary>The shortest time between two CloudCharge requests; tests set it to zero.</summary>
        internal TimeSpan RequestInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>The login state: loggedIn, loginRequired or codeSent.</summary>
        internal string CurrentLoginState => _loginState;

        // The HTTP call; the tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            DirectHttp.SendAsync(request, "CloudCharge", cancellationToken);

        public override void ConfigureDevice()
        {
            _client = null;
            _phoneNumber = "";
            _state = new DefaPowerState();
            _stateChanged = false;
            _loginState = LoginRequired;
            _lastPoll = null;
            _chargePointsRead = null;
            _smartChargingRead = null;
            lock (_lock)
            {
                _connectors = [];
                _publishedSeries.Clear();
            }
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _phoneNumber = CloudChargeClient.NormalizePhoneNumber(Options.PhoneNumber);
            _client = new CloudChargeClient(SendAsync, Logger, Time) { RequestInterval = RequestInterval };
            _state = LoadState();

            if (LoadLogin() is DefaPowerLogin login)
            {
                _client.SetLogin(login.UserId, login.Token);
                _loginState = LoggedIn;
                SendLoginState();
                // CloudCharge being unavailable or the login being gone doesn't stop the device: the loop polls again,
                // and a lost login waits for the login commands.
                await PollAsync(force: true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _loginState = LoginRequired;
                SendLoginState();
                Logger.LogWarning("DEFA Power isn't logged in to CloudCharge: send the {Request} command, then the {Login} command with the code from the SMS.",
                    RequestSmsCode, Login);
            }

            if (RunLoop)
            {
                _loopStop = new CancellationTokenSource();
                var stop = _loopStop.Token;
                _loop = Task.Run(() => LoopAsync(stop), CancellationToken.None);
            }
        }

        protected override async Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            var stop = _loopStop;
            var loop = _loop;
            _loopStop = null;
            _loop = null;
            if (stop != null)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (loop != null)
                {
                    try
                    {
                        await loop.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        Logger.LogWarning("DEFA Power's poll loop didn't stop in 10 seconds.");
                    }
                }
                stop.Dispose();
            }
            if (_stateChanged)
                SaveState();
            _client = null;
        }

        // Called by the scheduler when a refresh schedule is set; the device polls on its own.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : RefreshNowAsync(cancellationToken);

        private async Task RefreshNowAsync(CancellationToken cancellationToken)
        {
            if (_lastPoll is DateTimeOffset last && Time.GetUtcNow() - last < MinimumRefreshInterval)
            {
                Logger.LogDebug("CloudCharge was polled less than {Seconds} seconds ago; the refresh is skipped.", MinimumRefreshInterval.TotalSeconds);
                return;
            }
            await PollAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(NextDelay(), Time, cancellationToken).ConfigureAwait(false);
                    await PollAsync(force: false, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    Logger.LogError(error, "DEFA Power's poll failed.");
                }
            }
        }

        /// <summary>
        /// The wait until the next poll: <see cref="ChargingInterval"/> while a connector charges, <see cref="IdleInterval"/>
        /// otherwise, and never past a quarter-hour, so every quarter's energy is read just after it ends.
        /// </summary>
        internal TimeSpan NextDelay()
        {
            var now = Time.GetUtcNow();
            bool charging;
            lock (_lock)
                charging = _connectors.Any(x => x.Charging);
            var interval = charging ? ChargingInterval : IdleInterval;
            var quarter = DefaPowerEnergy.QuarterStart(now) + DefaPowerEnergy.Quarter + _quarterDelay;
            var untilQuarter = quarter - now;
            if (untilQuarter > DefaPowerEnergy.Quarter)
                untilQuarter -= DefaPowerEnergy.Quarter;
            var delay = untilQuarter < interval ? untilQuarter : interval;
            return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        }

        /// <summary>
        /// Reads the chargers and reports their state and energy. <paramref name="force"/> reads the charge points and the
        /// smart charging settings too; otherwise they are read every <see cref="ChargePointInterval"/> and
        /// <see cref="SmartChargingInterval"/>. CloudCharge's errors are logged, the last state is kept; a refused login
        /// waits for the login commands.
        /// </summary>
        internal async Task PollAsync(bool force, CancellationToken cancellationToken)
        {
            if (_client is not { LoggedIn: true } client)
                return;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ReadAsync(client, force, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudChargeAuthenticationException error)
            {
                LoginLost(error.Message);
            }
            catch (CloudChargeUnavailableException error)
            {
                Logger.LogWarning("Could not read the DEFA Power chargers: {Reason}", error.Message);
            }
            catch (CloudChargeRefusedException error)
            {
                Logger.LogWarning("CloudCharge refused to read the DEFA Power chargers: {Reason}", error.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ReadAsync(CloudChargeClient client, bool force, CancellationToken cancellationToken)
        {
            var now = Time.GetUtcNow();
            List<DefaPowerConnector> previous;
            lock (_lock)
                previous = _connectors.Select(x => x.Copy()).ToList();
            var wasCharging = previous.Where(x => x.Charging).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

            var connectors = previous;
            if (force || connectors.Count == 0 || _chargePointsRead is not DateTimeOffset read || now - read >= ChargePointInterval)
            {
                connectors = await ReadChargePointsAsync(client, previous, cancellationToken).ConfigureAwait(false);
                _chargePointsRead = now;
                if (connectors.Count == 0)
                    Logger.LogWarning("The CloudCharge account has no chargers.");
            }

            foreach (var connector in connectors)
                await client.ReadOperationalDataAsync(connector, cancellationToken).ConfigureAwait(false);

            if (force || _smartChargingRead is not DateTimeOffset smart || now - smart >= SmartChargingInterval)
            {
                foreach (var connector in connectors)
                    await ReadSmartChargingAsync(client, connector, cancellationToken).ConfigureAwait(false);
                _smartChargingRead = now;
            }

            foreach (var connector in connectors.Where(x => x.Charging && !wasCharging.Contains(x.Id)))
            {
                try
                {
                    await client.StartLiveConsumptionAsync(connector.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is CloudChargeRefusedException or CloudChargeUnavailableException)
                {
                    Logger.LogDebug("CloudCharge didn't start the live consumption of {Connector}: {Reason}", connector.Name, error.Message);
                }
            }

            lock (_lock)
                _connectors = connectors;
            _lastPoll = now;

            var sampled = Time.GetUtcNow();
            foreach (var connector in connectors)
            {
                var state = Account(connector, sampled);
                SendStateReports(connector, state);
                SendEnergyReports(connector, state, sampled);
            }
            if (_stateChanged)
                SaveState();
        }

        // The charge points again; a connector keeps what the operational data and the smart charging reads gave it.
        private async Task<List<DefaPowerConnector>> ReadChargePointsAsync(CloudChargeClient client, List<DefaPowerConnector> previous,
            CancellationToken cancellationToken)
        {
            var connectors = new List<DefaPowerConnector>();
            foreach (var id in await client.GetChargePointIdsAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var connector in await client.GetConnectorsAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    if (connectors.Any(x => x.Id == connector.Id))
                        continue;
                    if (previous.FirstOrDefault(x => x.Id == connector.Id) is DefaPowerConnector known)
                    {
                        connector.ChargingState ??= known.ChargingState;
                        connector.TransactionEnergy = known.TransactionEnergy;
                        connector.EcoMode = known.EcoMode;
                        connector.EcoHoursToCharge = known.EcoHoursToCharge;
                        connector.ManualSchedules = known.ManualSchedules;
                        connector.PausedBy = known.PausedBy;
                        connector.PausedUntil = known.PausedUntil;
                        connector.ScheduleOverridden = known.ScheduleOverridden;
                        connector.CurrentAlternatives = known.CurrentAlternatives;
                    }
                    connectors.Add(connector);
                }
            }
            return connectors;
        }

        // A charger may claim a capability its firmware refuses; that doesn't fail the poll.
        private async Task ReadSmartChargingAsync(CloudChargeClient client, DefaPowerConnector connector, CancellationToken cancellationToken)
        {
            try
            {
                if (connector.EcoModeCapable)
                    await client.ReadEcoModeAsync(connector, cancellationToken).ConfigureAwait(false);
                if (connector.ManualSchedulesCapable)
                {
                    await client.ReadManualSchedulesAsync(connector, cancellationToken).ConfigureAwait(false);
                    await client.ReadActiveScheduleAsync(connector, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (CloudChargeRefusedException error)
            {
                Logger.LogDebug("CloudCharge refused the smart charging settings of {Connector}: {Reason}", connector.Name, error.Message);
            }
        }

        // The meter sample goes into the quarters; a car plugged in opens a session, one unplugged closes it.
        private DefaPowerConnectorState Account(DefaPowerConnector connector, DateTimeOffset time)
        {
            if (!_state.Connectors.TryGetValue(connector.Id, out var state))
            {
                state = new DefaPowerConnectorState();
                _state.Connectors[connector.Id] = state;
                _stateChanged = true;
            }
            if (connector.Meter is not double meter)
                return state;

            var sample = DefaPowerEnergy.AddSample(state, time, meter);
            if (sample == DefaPowerSample.First)
                _stateChanged = true;
            else if (sample == DefaPowerSample.MeterReset)
                Logger.LogWarning("The meter of {Connector} went back to {Meter} kWh; the energy since the previous reading is lost.", connector.Name, meter);

            var connected = DefaPowerStates.Connected(connector.ChargingState, connector.Status);
            if (connected == true && state.Session == null)
            {
                state.Session = new DefaPowerOpenSession { Start = time, StartMeter = meter };
                _stateChanged = true;
                Logger.LogInformation("A car was plugged in to {Connector}.", connector.Name);
            }
            else if (connected == false && state.Session is DefaPowerOpenSession open)
            {
                var energy = Math.Round(Math.Max(0, meter - open.StartMeter), 3);
                var cost = DefaPowerEnergy.CostBetween(state, open.Start, time, _state.Prices, _state.PriceUnit);
                state.Session = null;
                _stateChanged = true;
                SendSessionReport(connector, new DefaPowerSession(open.Start, time, energy, cost is double c ? Math.Round(c, 4) : null));
            }
            return state;
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId) ?? throw new UnknownCommandException(commandId);
            switch (command.Address)
            {
                case RequestSmsCode:
                    await RequestSmsCodeAsync(cancellationToken).ConfigureAwait(false);
                    return;
                case Login:
                    await LoginAsync(CommandText(value), cancellationToken).ConfigureAwait(false);
                    return;
                case RefreshAddress:
                    await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
                    return;
                case Prices:
                    await SetPricesAsync(value, cancellationToken).ConfigureAwait(false);
                    return;
            }

            var (connectorId, key) = SplitAddress(command.Address);
            if (connectorId == null)
                throw new UnknownCommandException(commandId);
            await ConnectorCommandAsync(commandId, connectorId, key, value, cancellationToken).ConfigureAwait(false);
        }

        private async Task RequestSmsCodeAsync(CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The DEFA Power device isn't started.");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await client.SendSmsCodeAsync(_phoneNumber, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudChargeRefusedException error)
            {
                throw new InvalidOperationException($"CloudCharge didn't send the SMS code: {error.Message}", error);
            }
            finally
            {
                _gate.Release();
            }
            if (!client.LoggedIn)
            {
                _loginState = CodeSent;
                SendLoginState();
            }
        }

        private async Task LoginAsync(string code, CancellationToken cancellationToken)
        {
            var client = _client ?? throw new DeviceNotReadyException("The DEFA Power device isn't started.");
            code = code.Trim();
            if (code.Length == 0)
                throw new ArgumentException("Send the code from the SMS with the login command.", nameof(code));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SaveLogin(await client.LoginAsync(_phoneNumber, code, cancellationToken).ConfigureAwait(false));
            }
            catch (CloudChargeRefusedException error)
            {
                throw new InvalidOperationException(
                    $"CloudCharge refused the login: {error.Message} Request a new code with {RequestSmsCode} and log in with it.", error);
            }
            finally
            {
                _gate.Release();
            }
            _loginState = LoggedIn;
            SendLoginState();
            Logger.LogInformation("DEFA Power logged in to CloudCharge as {Phone}.", CloudChargeClient.Mask(_phoneNumber));
            await PollAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        private void LoginLost(string reason)
        {
            _client?.ForgetLogin();
            DeleteLogin();
            _loginState = LoginRequired;
            SendLoginState();
            Logger.LogWarning("DEFA Power lost its CloudCharge login ({Reason}); send the {Request} and {Login} commands to log in again.",
                reason, RequestSmsCode, Login);
        }

        private async Task SetPricesAsync(string value, CancellationToken cancellationToken)
        {
            var series = ForecastSeries.Create(value) ?? ForecastSeries.Create(CommandText(value))
                ?? throw new ArgumentException("The prices are a price series: ElectricityPrice's prices report.", nameof(value));
            if (series.Validate() is string problem)
                throw new ArgumentException($"The price series isn't valid: {problem}", nameof(value));

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = Time.GetUtcNow();
                var taken = DefaPowerEnergy.MergePrices(_state, series, now);
                Logger.LogDebug("DEFA Power took {Count} prices in {Unit}.", taken, _state.PriceUnit ?? "c/kWh");
                if (taken == 0)
                    return;
                _stateChanged = true;
                List<DefaPowerConnector> connectors;
                lock (_lock)
                    connectors = _connectors.ToList();
                foreach (var connector in connectors)
                {
                    if (_state.Connectors.TryGetValue(connector.Id, out var state))
                        SendCostReports(connector, state, now);
                }
                SaveState();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ConnectorCommandAsync(string commandId, string connectorId, string key, string value, CancellationToken cancellationToken)
        {
            var client = _client;
            if (client is not { LoggedIn: true })
                throw new DeviceNotReadyException($"DEFA Power isn't logged in: send the {RequestSmsCode} and {Login} commands first.");
            if (FindConnector(connectorId) == null && _lastPoll == null)
            {
                // CloudCharge was unavailable at start: read the chargers now.
                await PollAsync(force: true, cancellationToken).ConfigureAwait(false);
            }

            DefaPowerConnector connector;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                connector = FindConnector(connectorId) ?? throw new DeviceNotReadyException($"DEFA Power connector {connectorId} isn't loaded yet.");
                switch (key)
                {
                    case Charging:
                        if (CommandBool(value))
                            await client.StartChargingAsync(connector.Alias, cancellationToken).ConfigureAwait(false);
                        else
                            await client.StopChargingAsync(connector.Alias, cancellationToken).ConfigureAwait(false);
                        break;
                    case CurrentLimit:
                        var current = WholeNumber(value, "current limit");
                        if (connector.CurrentAlternatives.Count == 0)
                            connector.CurrentAlternatives = await client.GetCurrentAlternativesAsync(connector.Id, cancellationToken).ConfigureAwait(false);
                        CheckCurrent(current, connector.CurrentAlternatives);
                        await client.SetCurrentLimitAsync(connector.Id, current, cancellationToken).ConfigureAwait(false);
                        connector.CurrentLimit = current;
                        break;
                    case EcoMode:
                        RequireCapability(connector.EcoModeCapable, connector, "eco mode");
                        await client.UpdateEcoModeAsync(connector, CommandBool(value), null, cancellationToken).ConfigureAwait(false);
                        _smartChargingRead = null;
                        break;
                    case EcoHoursToCharge:
                        RequireCapability(connector.EcoModeCapable, connector, "eco mode");
                        var hours = WholeNumber(value, "hours to charge");
                        if (hours < MinHoursToCharge || hours > MaxHoursToCharge)
                            throw new ArgumentOutOfRangeException(nameof(value), hours, $"The hours to charge must be {MinHoursToCharge}-{MaxHoursToCharge}.");
                        await client.UpdateEcoModeAsync(connector, null, hours, cancellationToken).ConfigureAwait(false);
                        _smartChargingRead = null;
                        break;
                    case ManualSchedules:
                        RequireCapability(connector.ManualSchedulesCapable, connector, "manual schedules");
                        var enabled = CommandBool(value);
                        await client.SetManualSchedulesAsync(connector.Id, enabled, cancellationToken).ConfigureAwait(false);
                        connector.ManualSchedules = enabled;
                        _smartChargingRead = null;
                        break;
                    case ChargeNow:
                        await client.OverrideScheduleAsync(connector, cancellationToken).ConfigureAwait(false);
                        _smartChargingRead = null;
                        break;
                    case Restart:
                        var type = CommandText(value).Trim().ToLowerInvariant();
                        if (type is "" or "true" or "1")
                            type = "soft";
                        if (!_restartTypes.Contains(type))
                            throw new ArgumentException($"The restart is one of {string.Join(", ", _restartTypes)}.", nameof(value));
                        await client.RestartAsync(connector.Id, type, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new UnknownCommandException(commandId);
                }
                lock (_lock)
                    _connectors = _connectors.Select(x => x.Id == connector.Id ? connector : x).ToList();
            }
            catch (CloudChargeRefusedException error)
            {
                throw new InvalidOperationException($"CloudCharge refused the command: {error.Message}", error);
            }
            catch (CloudChargeAuthenticationException error)
            {
                LoginLost(error.Message);
                throw new InvalidOperationException(error.Message, error);
            }
            finally
            {
                _gate.Release();
            }

            // The charger applies a command with a delay; report what was set now, the next poll confirms it.
            SendStateReports(connector, _state.Connectors.GetValueOrDefault(connector.Id));
        }

        private static void CheckCurrent(int current, List<int> alternatives)
        {
            if (alternatives.Count > 0 ? !alternatives.Contains(current) : current < MinCurrent || current > MaxCurrent)
                throw new ArgumentOutOfRangeException(nameof(current), current, alternatives.Count > 0
                    ? $"The charger takes a current limit of {string.Join(", ", alternatives)} A."
                    : $"The current limit must be {MinCurrent}-{MaxCurrent} A.");
        }

        private static void RequireCapability(bool capable, DefaPowerConnector connector, string what)
        {
            if (!capable)
                throw new InvalidOperationException($"{connector.Name} has no {what}.");
        }

        private DefaPowerConnector? FindConnector(string connectorId)
        {
            lock (_lock)
                return _connectors.FirstOrDefault(x => x.Id == connectorId)?.Copy();
        }

        private ReportTemplate? FindTemplate(string address) =>
            ReportTemplates?.FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));

        private ReportTemplate? FindTemplate(string connectorId, string key) => FindTemplate(Address(connectorId, key));

        internal static string Address(string connectorId, string key) => $"{connectorId}|{key}";

        private static (string? ConnectorId, string Key) SplitAddress(string? address)
        {
            var separator = address?.LastIndexOf('|') ?? -1;
            return separator <= 0 ? (null, "") : (address![..separator], address[(separator + 1)..]);
        }

        private void SendLoginState()
        {
            if (FindTemplate(LoginState) is ReportTemplate template)
                Send(template, new ValueModel(_loginState), Time.GetUtcNow(), always: false);
        }

        /// <summary>The state report values by key; a value the connector doesn't have is left out.</summary>
        private IEnumerable<(string Key, object? Value)> Values(DefaPowerConnector connector, DefaPowerConnectorState? state)
        {
            yield return (Status, connector.Status);
            yield return (ChargingState, connector.ChargingState);
            yield return (Charging, connector.ChargingState == null ? null : connector.Charging);
            yield return (Power, connector.Power is double power ? Math.Round(power, 3) : null);
            yield return (CurrentLimit, connector.CurrentLimit);
            yield return (Online, connector.Online);
            yield return (ErrorCode, connector.ErrorCode);
            yield return (Meter, connector.Meter is double meter ? Math.Round(meter, 3) : null);
            yield return (SessionEnergy, SessionEnergyOf(connector, state));
            yield return (EcoMode, connector.EcoMode);
            yield return (EcoHoursToCharge, connector.EcoHoursToCharge);
            yield return (ManualSchedules, connector.ManualSchedules);
            yield return (PausedBy, connector.PausedBy);
            yield return (PausedUntil, connector.PausedBy == null ? null : Iso(connector.PausedUntil));
            yield return (ScheduleOverridden, connector.ScheduleOverridden);
        }

        // The charger's own figure for the transaction; without it, the meter's increase since the car was plugged in.
        private static double? SessionEnergyOf(DefaPowerConnector connector, DefaPowerConnectorState? state)
        {
            if (connector.TransactionEnergy is double transaction)
                return Math.Round(transaction, 3);
            if (state?.Session is DefaPowerOpenSession open && connector.Meter is double meter)
                return Math.Round(Math.Max(0, meter - open.StartMeter), 3);
            return state == null ? null : 0d;
        }

        private void SendStateReports(DefaPowerConnector connector, DefaPowerConnectorState? state)
        {
            var now = Time.GetUtcNow();
            foreach (var (key, value) in Values(connector, state))
            {
                if (value != null && FindTemplate(connector.Id, key) is ReportTemplate template)
                    Send(template, new ValueModel(value), now, always: false);
            }
        }

        // Each finished quarter and hour once, time-stamped to its start; after a restart the ones it missed, at most two
        // days. The series of the last two days is sent when it changes.
        private void SendEnergyReports(DefaPowerConnector connector, DefaPowerConnectorState state, DateTimeOffset now)
        {
            var quarters = DefaPowerEnergy.FinishedQuarters(state);
            if (FindTemplate(connector.Id, EnergyQuarter) is ReportTemplate quarterTemplate && quarters.Count > 0)
            {
                var due = Due(quarters, state.LastReportedQuarter);
                foreach (var quarter in due)
                    Send(quarterTemplate, new ValueModel(Math.Round(quarter.Energy, 4)), quarter.Start, always: true);
                if (due.Count > 0)
                {
                    state.LastReportedQuarter = due[^1].Start;
                    _stateChanged = true;
                }
            }

            var hours = DefaPowerEnergy.FinishedHours(state);
            if (FindTemplate(connector.Id, EnergyHour) is ReportTemplate hourTemplate && hours.Count > 0)
            {
                var due = Due(hours, state.LastReportedHour);
                foreach (var hour in due)
                    Send(hourTemplate, new ValueModel(Math.Round(hour.Energy, 4)), hour.Start, always: true);
                if (due.Count > 0)
                {
                    state.LastReportedHour = due[^1].Start;
                    _stateChanged = true;
                }
            }

            if (FindTemplate(connector.Id, EnergyQuarterly) is ReportTemplate seriesTemplate && quarters.Count > 0)
            {
                var key = string.Join(";", quarters.Select(x => $"{x.Start.UtcTicks}:{Math.Round(x.Energy, 4).ToString(CultureInfo.InvariantCulture)}"));
                var changed = false;
                lock (_lock)
                {
                    if (!_publishedSeries.TryGetValue(seriesTemplate.Id, out var published) || published != key)
                    {
                        _publishedSeries[seriesTemplate.Id] = key;
                        changed = true;
                    }
                }
                if (changed)
                {
                    var series = new ForecastSeries
                    {
                        IssuedAt = now,
                        Kind = ForecastSeries.ConfirmedKind,
                        Unit = "kWh",
                        Resolution = "PT15M",
                        Points = quarters.Select(x => new ForecastPoint(x.Start, x.Start + DefaPowerEnergy.Quarter, Math.Round(x.Energy, 4))).ToList()
                    };
                    Send(seriesTemplate, series.ToValue(), now, always: true);
                }
            }

            if (FindTemplate(connector.Id, EnergyToday) is ReportTemplate todayTemplate && state.SampleTime != null)
                Send(todayTemplate, new ValueModel(Math.Round(DefaPowerEnergy.EnergySince(state, LocalMidnight(now)), 3)), now, always: false);

            SendCostReports(connector, state, now);
        }

        // The cost of each finished quarter once, when its price is known; a quarter whose price never comes is skipped once
        // later prices are known. And the cost since midnight, when every quarter with energy has a price.
        private void SendCostReports(DefaPowerConnector connector, DefaPowerConnectorState state, DateTimeOffset now)
        {
            if (_state.Prices.Count == 0)
                return;

            var quarters = DefaPowerEnergy.FinishedQuarters(state);
            if (FindTemplate(connector.Id, CostQuarter) is ReportTemplate costTemplate && quarters.Count > 0)
            {
                var pricedUntil = _state.Prices.Max(x => x.End);
                DateTimeOffset? last = null;
                foreach (var quarter in Due(quarters, state.LastCostedQuarter))
                {
                    if (DefaPowerEnergy.QuarterCost(quarter, _state.Prices, _state.PriceUnit) is double cost)
                        Send(costTemplate, new ValueModel(Math.Round(cost, 4)), quarter.Start, always: true);
                    else if (quarter.Start + DefaPowerEnergy.Quarter > pricedUntil)
                        break;
                    last = quarter.Start;
                }
                if (last != null)
                {
                    state.LastCostedQuarter = last;
                    _stateChanged = true;
                }
            }

            if (FindTemplate(connector.Id, CostToday) is ReportTemplate todayTemplate &&
                DefaPowerEnergy.CostBetween(state, LocalMidnight(now), now, _state.Prices, _state.PriceUnit) is double today)
                Send(todayTemplate, new ValueModel(Math.Round(today, 4)), now, always: false);
        }

        // The ones after the last reported; the first time all of them, which start at the device's first meter reading.
        private static List<DefaPowerQuarter> Due(List<DefaPowerQuarter> finished, DateTimeOffset? lastReported) =>
            lastReported is DateTimeOffset last ? finished.Where(x => x.Start > last).ToList() : finished;

        // Each session once, time-stamped at its end.
        private void SendSessionReport(DefaPowerConnector connector, DefaPowerSession session)
        {
            Logger.LogInformation("{Connector} charging session ended: {Energy} kWh in {Minutes} min.", connector.Name,
                session.Energy.ToString(CultureInfo.InvariantCulture), Minutes(session));
            if (FindTemplate(connector.Id, Session) is not ReportTemplate template)
                return;
            var value = new Dictionary<string, object?>
            {
                ["start"] = Iso(session.Start),
                ["end"] = Iso(session.End),
                ["durationMinutes"] = Minutes(session),
                ["energy"] = session.Energy,
                ["cost"] = session.Cost,
                ["costUnit"] = session.Cost == null ? null : DefaPowerEnergy.CostUnit(_state.PriceUnit)
            };
            Send(template, new ValueModel(value), session.End, always: true);
        }

        private static int Minutes(DefaPowerSession session) => (int)Math.Round((session.End - session.Start).TotalMinutes);

        private void Send(ReportTemplate template, ValueModel value, DateTimeOffset at, bool always)
        {
            var report = new Report { Id = template.Id, TimeStamp = at.ToUnixTimeSeconds(), Value = value, Filter = "" };
            if (always)
                SendReport(this, report);
            else
                SendReportIfValueChanged(this, report);
        }

        private string Iso(DateTimeOffset? time) =>
            time is DateTimeOffset value
                ? TimeZoneInfo.ConvertTime(value, Time.LocalTimeZone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)
                : "";

        private DateTimeOffset LocalMidnight(DateTimeOffset now)
        {
            var zone = Time.LocalTimeZone;
            var local = TimeZoneInfo.ConvertTime(now, zone).Date;
            while (zone.IsInvalidTime(local))
                local = local.AddMinutes(30);
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }

        internal string LoginFile => Path.Combine(DataDirectory, $"defapower-auth-{Hash(_phoneNumber)}.json");

        internal string StateFile => Path.Combine(DataDirectory, $"defapower-{Hash(_phoneNumber)}.json");

        private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

        private DefaPowerLogin? LoadLogin()
        {
            var login = Read<DefaPowerLogin>(LoginFile, "login");
            return login is { UserId.Length: > 0, Token.Length: > 0 } ? login : null;
        }

        private void SaveLogin(DefaPowerLogin login) => Write(LoginFile, login, "login");

        private void DeleteLogin()
        {
            try
            {
                File.Delete(LoginFile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Could not delete the DEFA Power login file {File}.", LoginFile);
            }
        }

        private DefaPowerState LoadState() => Read<DefaPowerState>(StateFile, "state") ?? new DefaPowerState();

        private void SaveState()
        {
            Write(StateFile, _state, "state");
            _stateChanged = false;
        }

        private T? Read<T>(string file, string what) where T : class
        {
            try
            {
                return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.LogWarning(error, "Could not read the DEFA Power {What} file {File}.", what, file);
                return null;
            }
        }

        private void Write<T>(string file, T value, string what)
        {
            try
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(value));
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(error, "Could not save the DEFA Power {What} file {File}.", what, file);
            }
        }

        // A command value arrives as JSON: 16, "soft", true. A bare word is taken as text.
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

        private static string CommandText(string value)
        {
            var json = CommandJson(value);
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString() ?? "",
                JsonValueKind.Number => json.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value
            };
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
                    if (text is "true" or "on" or "1" or "start")
                        return true;
                    if (text is "false" or "off" or "0" or "stop")
                        return false;
                    break;
            }
            throw new ArgumentException($"'{value}' is not true or false.", nameof(value));
        }

        private static int WholeNumber(string value, string what)
        {
            var json = CommandJson(value);
            double number;
            if (json.ValueKind == JsonValueKind.Number)
                number = json.GetDouble();
            else if (json.ValueKind != JsonValueKind.String ||
                !double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new ArgumentException($"'{value}' is not a number.", nameof(value));
            if (number != Math.Floor(number) || number < int.MinValue || number > int.MaxValue)
                throw new ArgumentException($"The {what} is a whole number.", nameof(value));
            return (int)number;
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var reports = new List<ReportTemplate>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = LoginState,
                    Name = "DEFA Power login",
                    Type = ValueType.Text,
                    Model = LoginRequired,
                    Schema = Choices(_loginStates)
                }
            };
            var commands = DeviceCommands().ToList();
            var configuration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "DEFA Power",
                ClassFullName = GetType().FullName!,
                // The device polls on its own: every 15 seconds while charging, every minute otherwise.
                RefreshSchedule = null,
                DeviceParameters = new Dictionary<string, string>
                {
                    ["phoneNumber"] = "+358401234567"
                },
                ReportTemplates = reports,
                CommandTemplates = commands
            };

            if (State != DeviceState.Running)
                return configuration;

            List<DefaPowerConnector> connectors;
            lock (_lock)
                connectors = _connectors.ToList();
            foreach (var connector in connectors)
            {
                reports.AddRange(ConnectorReports(connector));
                commands.AddRange(ConnectorCommands(connector));
            }
            return configuration;
        }

        private static IEnumerable<CommandTemplate> DeviceCommands()
        {
            yield return new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "DEFA Power request SMS code",
                Address = RequestSmsCode,
                Type = ValueType.Boolean,
                Model = true
            };
            var code = ValueSchema.Text();
            code.Description = "The code from the SMS";
            yield return new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "DEFA Power login",
                Address = Login,
                Type = ValueType.Text,
                Model = "",
                Schema = code
            };
            yield return new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Refresh DEFA Power",
                Address = RefreshAddress,
                Type = ValueType.Boolean,
                Model = true
            };
            var prices = ValueSchema.Forecast(new ValueSchema { Type = "number", Unit = "c/kWh", Title = "Price" });
            prices.Description = "Electricity prices for the cost reports: forward ElectricityPrice's prices report here";
            yield return new CommandTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Name = "DEFA Power electricity prices",
                Address = Prices,
                Type = ValueType.Entity,
                Model = null,
                Schema = prices
            };
        }

        private static IEnumerable<ReportTemplate> ConnectorReports(DefaPowerConnector connector)
        {
            var kwh = ValueSchema.Number("kWh", ValueSemantics.Energy, 0);
            yield return Report(connector, Status, "status", ValueType.Text, connector.Status ?? "available", Choices(DefaPowerStates.Statuses));
            yield return Report(connector, ChargingState, "charging state", ValueType.Text, connector.ChargingState ?? DefaPowerStates.Idle,
                Choices(DefaPowerStates.ChargingStates));
            yield return Report(connector, Charging, "charging", ValueType.Boolean, connector.Charging, ValueSchema.Boolean());
            yield return Report(connector, Power, "power", ValueType.Number, connector.Power ?? 0, ValueSchema.Number("kW", ValueSemantics.Power, 0), "kW");
            yield return Report(connector, CurrentLimit, "current limit", ValueType.Number, connector.CurrentLimit ?? MaxCurrent,
                ValueSchema.Integer("A", ValueSemantics.Current, MinCurrent, MaxCurrent), "A");
            yield return Report(connector, Online, "online", ValueType.Boolean, connector.Online ?? true, ValueSchema.Boolean());
            yield return Report(connector, ErrorCode, "error code", ValueType.Text, connector.ErrorCode ?? "", ValueSchema.Text());
            yield return Report(connector, Meter, "meter", ValueType.Number, connector.Meter ?? 0, kwh, "kWh");
            yield return Report(connector, SessionEnergy, "session energy", ValueType.Number, 0, kwh, "kWh");
            yield return Report(connector, EnergyQuarter, "energy per quarter-hour", ValueType.Number, 0, kwh, "kWh");
            yield return Report(connector, EnergyHour, "energy per hour", ValueType.Number, 0, kwh, "kWh");
            yield return Report(connector, EnergyToday, "energy today", ValueType.Number, 0, kwh, "kWh");
            yield return Report(connector, EnergyQuarterly, "quarter-hourly energy", ValueType.Entity, null,
                ValueSchema.Forecast(new ValueSchema { Type = "number", Unit = "kWh", Semantic = ValueSemantics.Energy, Title = "Energy" }), "kWh");
            yield return Report(connector, Session, "charging session", ValueType.Entity, null, ValueSchema.Entity(new Dictionary<string, ValueSchema>
            {
                ["start"] = ValueSchema.Text(),
                ["end"] = ValueSchema.Text(),
                ["durationMinutes"] = ValueSchema.Integer("min"),
                ["energy"] = ValueSchema.Number("kWh", ValueSemantics.Energy),
                ["cost"] = ValueSchema.Number(),
                ["costUnit"] = ValueSchema.Text()
            }));
            yield return Report(connector, CostQuarter, "cost per quarter-hour", ValueType.Number, 0, ValueSchema.Number("€"), "€");
            yield return Report(connector, CostToday, "cost today", ValueType.Number, 0, ValueSchema.Number("€"), "€");
            if (connector.EcoModeCapable)
            {
                yield return Report(connector, EcoMode, "eco mode", ValueType.Boolean, connector.EcoMode ?? false, ValueSchema.Boolean());
                yield return Report(connector, EcoHoursToCharge, "eco mode hours to charge", ValueType.Number, connector.EcoHoursToCharge ?? 4,
                    ValueSchema.Integer("h", null, MinHoursToCharge, MaxHoursToCharge), "h");
            }
            if (connector.ManualSchedulesCapable)
                yield return Report(connector, ManualSchedules, "manual schedules", ValueType.Boolean, connector.ManualSchedules ?? false, ValueSchema.Boolean());
            if (connector.EcoModeCapable || connector.ManualSchedulesCapable)
            {
                yield return Report(connector, PausedBy, "paused by", ValueType.Text, connector.PausedBy ?? DefaPowerStates.PausedByNone,
                    Choices(DefaPowerStates.PausedBy));
                yield return Report(connector, PausedUntil, "paused until", ValueType.Text, "", ValueSchema.Text());
                yield return Report(connector, ScheduleOverridden, "charging now", ValueType.Boolean, connector.ScheduleOverridden ?? false, ValueSchema.Boolean());
            }
        }

        private static IEnumerable<CommandTemplate> ConnectorCommands(DefaPowerConnector connector)
        {
            yield return Command(connector, Charging, "start or stop charging", ValueType.Boolean, true, ValueSchema.Boolean(ValueSemantics.Switch));
            var current = ValueSchema.Integer("A", ValueSemantics.Current, MinCurrent, MaxCurrent);
            if (connector.CurrentAlternatives.Count > 0)
                current.Enum = connector.CurrentAlternatives.Cast<object>().ToList();
            yield return Command(connector, CurrentLimit, "set current limit", ValueType.Number, connector.CurrentLimit ?? 16, current);
            if (connector.EcoModeCapable)
            {
                yield return Command(connector, EcoMode, "set eco mode", ValueType.Boolean, true, ValueSchema.Boolean());
                yield return Command(connector, EcoHoursToCharge, "set eco mode hours to charge", ValueType.Number, connector.EcoHoursToCharge ?? 4,
                    ValueSchema.Integer("h", null, MinHoursToCharge, MaxHoursToCharge));
            }
            if (connector.ManualSchedulesCapable)
                yield return Command(connector, ManualSchedules, "set manual schedules", ValueType.Boolean, true, ValueSchema.Boolean());
            if (connector.EcoModeCapable || connector.ManualSchedulesCapable)
                yield return Command(connector, ChargeNow, "charge now", ValueType.Boolean, true, ValueSchema.Boolean());
            yield return Command(connector, Restart, "restart", ValueType.Text, "soft", Choices(_restartTypes));
        }

        private static ValueSchema Choices(IReadOnlyList<string> names)
        {
            var schema = ValueSchema.Text();
            schema.Enum = names.Cast<object>().ToList();
            return schema;
        }

        private static ReportTemplate Report(DefaPowerConnector connector, string key, string name, ValueType type, object? model, ValueSchema schema,
            string? unit = null)
        {
            var template = new ReportTemplate
            {
                Id = Guid.NewGuid().ToString(),
                Address = Address(connector.Id, key),
                Name = $"{connector.Name} {name}",
                Type = type,
                Model = model,
                Schema = schema
            };
            if (unit != null)
                template.Parameters["unit"] = unit;
            return template;
        }

        private static CommandTemplate Command(DefaPowerConnector connector, string key, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = Address(connector.Id, key),
            Name = $"{connector.Name} {name}",
            Type = type,
            Model = model,
            Schema = schema
        };
    }
}
