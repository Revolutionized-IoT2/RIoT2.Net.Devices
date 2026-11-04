using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="NetatmoSecurity"/> device's parameters: the Netatmo app, the home and the webhook.</summary>
    public sealed class NetatmoSecurityOptions : NetatmoOptions
    {
        [DeviceParameter("homeId", Description = "Id of the Netatmo home to show; empty: the home with security modules")]
        public string HomeId { get; set; } = "";

        [DeviceParameter("webhookUrl", Description = "Public base URL Netatmo pushes events to, for example https://netatmo.example.com; empty: polling only")]
        public string WebhookUrl { get; set; } = "";

        [DeviceParameter("webhookSecret", Secret = true, Description = "Random secret of at least 32 letters, digits, '-' or '_'; the last segment of the webhook URL")]
        public string WebhookSecret { get; set; } = "";

        [DeviceParameter("webhookTakeover", Default = "false", Description = "Replace another webhook registered for the Netatmo app, for example Home Assistant's")]
        public bool WebhookTakeover { get; set; }

        [DeviceParameter("downloadSnapshots", Default = "true", Description = "Keep event snapshots in the node's memory storage")]
        public bool DownloadSnapshots { get; set; } = true;
    }

    /// <summary>
    /// Netatmo's security products: cameras, the doorbell, door tags, the siren, and smoke and CO alarms. Events arrive
    /// through Netatmo's webhook when <c>webhookUrl</c> is set, and through polling, which only reconciles while the
    /// webhook works. Both go through one handling that drops repeats.
    /// </summary>
    [RequiresHardware("Netatmo API")]
    public class NetatmoSecurity : NetatmoBase<NetatmoSecurityOptions>, IDeviceWithConfiguration, IAsyncCommandDevice
    {
        internal const string WebhookRoute = "api/netatmo/webhook";

        /// <summary>While the webhook works, a refresh polls only this often, to catch pushes Netatmo didn't deliver.</summary>
        internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(15);

        private static readonly HashSet<string> SecurityTypes = new(StringComparer.OrdinalIgnoreCase) { "NACamera", "NOC", "NPC", "NDB", "NACamDoorTag", "NIS", "NSD", "NCO" };
        private static readonly HashSet<string> CameraTypes = new(StringComparer.OrdinalIgnoreCase) { "NACamera", "NOC", "NPC" };
        private static readonly HashSet<string> ConnectedTypes = new(StringComparer.OrdinalIgnoreCase) { "NACamera", "NOC", "NPC", "NDB" };
        private static readonly HashSet<string> BatteryTypes = new(StringComparer.OrdinalIgnoreCase) { "NACamDoorTag", "NIS", "NSD", "NCO" };
        private static readonly string[] SecurityScopes =
        [
            "read_camera", "access_camera", "write_camera", "read_presence", "access_presence", "write_presence",
            "read_doorbell", "access_doorbell", "read_smokedetector", "read_carbonmonoxidedetector"
        ];

        private readonly IMemoryStorageService _memoryStorageService;
        private readonly INetatmoWebhookHub _webhookHub;
        private readonly RecentKeys _seen = new(1000);
        private readonly SemaphoreSlim _eventGate = new(1, 1);
        private Home? _home;
        private string? _webhookUrl;
        private IDisposable? _subscription;
        private DateTimeOffset _lastPoll;
        // Whether the home's past events are remembered; until then a poll only remembers them.
        private bool _seeded;

        public NetatmoSecurity(ILogger logger, IMemoryStorageService memoryStorageService, INetatmoAuthClientFactory authClients, INetatmoWebhookHub webhookHub)
            : base(logger, authClients)
        {
            _memoryStorageService = memoryStorageService;
            _webhookHub = webhookHub ?? throw new ArgumentNullException(nameof(webhookHub));
        }

        protected override IReadOnlyCollection<string> Scopes => SecurityScopes;

        /// <summary>The URL the webhook is registered at; null without <c>webhookUrl</c>.</summary>
        internal string? WebhookUrl => _webhookUrl;

        internal NetatmoWebhookStatus WebhookStatus => _webhookUrl == null
            ? new NetatmoWebhookStatus(NetatmoWebhookState.Off, null)
            : _webhookHub.GetStatus(ClientId);

        protected override string? StatusNote => _webhookUrl == null ? null : WebhookStatus.Message;

        public override void ConfigureDevice()
        {
            ConfigureNetatmo();
            _home = null;
            _seeded = false;
            _webhookUrl = null;

            var baseUrl = Options.WebhookUrl?.Trim() ?? "";
            if (baseUrl.Length == 0)
                return;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("webhookUrl must be the public base URL Netatmo pushes to, for example https://netatmo.example.com.");
            if (!IsValidSecret(Options.WebhookSecret))
                throw new InvalidOperationException("webhookSecret must be at least 32 letters, digits, '-' or '_' when webhookUrl is set.");
            _webhookUrl = $"{baseUrl.TrimEnd('/')}/{WebhookRoute}/{Options.WebhookSecret}";
        }

        internal static bool IsValidSecret(string? secret) =>
            secret != null && secret.Length >= 32 && secret.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _seen.Clear();
            _seeded = false;
            _lastPoll = default;
            await CallNetatmoAsync(LoadHomeAsync, cancellationToken).ConfigureAwait(false);

            if (_webhookUrl != null)
            {
                _subscription = await _webhookHub.SubscribeAsync(new NetatmoWebhookSubscription(
                    Api, Options.WebhookSecret, _webhookUrl, Options.WebhookTakeover, _home?.id, HandleWebhookAsync), cancellationToken).ConfigureAwait(false);
                SetRunningMessage(StatusNote ?? StateMessage);
            }
            SendState("home|webhook", WebhookStatus.IsActive);
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override async Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken)
        {
            if (report != null || IsBackingOff)
                return;

            if (_webhookUrl != null)
                await _webhookHub.RecheckAsync(ClientId, cancellationToken).ConfigureAwait(false);
            SendState("home|webhook", WebhookStatus.IsActive);

            if (_seeded && WebhookStatus.IsActive && Now() - _lastPoll < ReconcileInterval)
            {
                SetRunningMessage(StatusNote ?? "");
                return;
            }
            await CallNetatmoAsync(PollOrLoadAsync, cancellationToken).ConfigureAwait(false);
        }

        // A poll; or, until the home and its past events are read, reading them.
        private Task PollOrLoadAsync(CancellationToken cancellationToken) =>
            _home == null || !_seeded ? LoadHomeAsync(cancellationToken) : PollAsync(seed: false, cancellationToken);

        protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
        {
            await CallNetatmoAsync(LoadHomeAsync, cancellationToken).ConfigureAwait(false);
            if (_webhookUrl != null)
                await _webhookHub.RecheckAsync(ClientId, cancellationToken).ConfigureAwait(false);
            SendState("home|webhook", WebhookStatus.IsActive);
        }

        /// <summary>Reads the homes, picks this device's, and takes its state; its past events are only remembered.</summary>
        private async Task LoadHomeAsync(CancellationToken cancellationToken)
        {
            var data = await Api.GetAsync<NetatmoHomesData>("api/homesdata", null, cancellationToken).ConfigureAwait(false);
            Api.Auth.Account = data?.body?.user?.email;
            _home = SelectHome(data?.body?.homes ?? []);
            await PollAsync(seed: true, cancellationToken).ConfigureAwait(false);
        }

        internal Home SelectHome(List<Home> homes)
        {
            string Describe() => string.Join(", ", homes.Select(h => $"{h.id} ({h.name})"));

            if (!string.IsNullOrWhiteSpace(Options.HomeId))
                return homes.FirstOrDefault(h => h.id == Options.HomeId.Trim())
                    ?? throw new InvalidOperationException($"The Netatmo account has no home {Options.HomeId}. Its homes: {Describe()}.");
            if (homes.Count == 0)
                throw new InvalidOperationException("The Netatmo account has no home.");

            var secured = homes.Where(h => h.modules?.Any(m => SecurityTypes.Contains(m.type)) == true).ToList();
            var home = secured.FirstOrDefault() ?? homes[0];
            if (homes.Count > 1)
                Logger.LogInformation("The Netatmo account has several homes ({Homes}); {Device} shows {Home}. Set homeId to choose another.", Describe(), Name, home.id);
            return home;
        }

        private async Task PollAsync(bool seed, CancellationToken cancellationToken)
        {
            var home = _home!;
            var query = new[] { KeyValuePair.Create("home_id", home.id) };
            var status = await Api.GetAsync<NetatmoHomeStatus>("api/homestatus", query, cancellationToken).ConfigureAwait(false);
            ApplyStatus(status?.body?.home);

            var events = await Api.GetAsync<NetatmoEvent>("api/getevents", query, cancellationToken).ConfigureAwait(false);
            // Newest first from Netatmo: handle them in the order they happened.
            foreach (var polled in (events?.body?.home?.events ?? []).OrderBy(e => e.time))
            {
                foreach (var securityEvent in NetatmoSecurityEvent.From(polled))
                {
                    if (seed)
                        Remember(securityEvent);
                    else
                        await HandleEventAsync(securityEvent, cancellationToken).ConfigureAwait(false);
                }
            }
            _lastPoll = Now();
            if (seed)
                _seeded = true;
        }

        private void ApplyStatus(HomeStatusHome? status)
        {
            foreach (var module in status?.modules ?? [])
            {
                if (module.monitoring != null)
                    SendState($"{module.id}|monitoring", module.monitoring == "on");
                var connected = module.reachable ?? (module.monitoring == null ? null : module.monitoring != "disconnected");
                SendState($"{module.id}|connection", connected);
                SendState($"{module.id}|floodlight", module.floodlight);
                SendState($"{module.id}|battery", module.battery_state);
            }
            foreach (var person in status?.persons ?? [])
            {
                if (person.out_of_sight != null)
                    SendState($"person|{person.id}", !person.out_of_sight.Value);
            }
        }

        private void Remember(NetatmoSecurityEvent securityEvent)
        {
            var category = Classify(securityEvent);
            if (category != null)
                _seen.Add(KeyOf(securityEvent, category.Value));
        }

        /// <summary>A push from the webhook hub, off the HTTP request.</summary>
        internal async Task HandleWebhookAsync(NetatmoWebhookEvent pushed, CancellationToken cancellationToken)
        {
            SendState("home|webhook", WebhookStatus.IsActive);
            var home = _home;
            if (pushed.IsActivation || pushed.IsDeactivation || home == null || State != DeviceState.Running)
                return;
            if (pushed.HomeId != null && pushed.HomeId != home.id)
                return;

            var moduleId = pushed.ModuleCandidates.FirstOrDefault(id => home.modules?.Any(m => m.id == id) == true)
                ?? pushed.ModuleCandidates.FirstOrDefault();
            foreach (var securityEvent in NetatmoSecurityEvent.From(pushed, moduleId))
                await HandleEventAsync(securityEvent, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Updates the state an event changes, and reports a detection once, whether it was pushed or polled.</summary>
        internal async Task HandleEventAsync(NetatmoSecurityEvent securityEvent, CancellationToken cancellationToken)
        {
            await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var type = securityEvent.Type.ToLowerInvariant();
                var module = securityEvent.ModuleId;
                switch (type)
                {
                    case "on":
                    case "off":
                        SendState($"{module}|monitoring", type == "on");
                        return;
                    case "connection":
                    case "boot":
                        SendState($"{module}|connection", true);
                        return;
                    case "disconnection":
                        SendState($"{module}|connection", false);
                        break;
                    case "light_mode":
                        SendState($"{module}|floodlight", securityEvent.Mode);
                        return;
                    case "person_home":
                        SendState($"person|{securityEvent.PersonId}", true);
                        return;
                    case "person_away":
                        SendState($"person|{securityEvent.PersonId}", false);
                        return;
                    case "module_low_battery":
                        SendState($"{module}|battery", "low");
                        return;
                    case "person" when PersonName(securityEvent.PersonId) != null && securityEvent.IsKnown != false:
                        SendState($"person|{securityEvent.PersonId}", true);
                        break;
                }

                var category = Classify(securityEvent);
                if (category == null)
                {
                    Logger.LogDebug("Ignored Netatmo event {Type} of {Module}.", securityEvent.Type, module);
                    return;
                }
                if (!_seen.Add(KeyOf(securityEvent, category.Value)))
                    return;

                var template = ReportAt($"{module}|event");
                if (template == null)
                    return;

                var report = new SecurityReport
                {
                    Source = "netatmo",
                    ImageUrl = securityEvent.SnapshotUrl ?? securityEvent.VignetteUrl,
                    Message = securityEvent.Message ?? $"{securityEvent.Type} ({ModuleName(module)})",
                    SecurityEvent = category.Value,
                    EventValue = PersonName(securityEvent.PersonId) ?? (object?)securityEvent.SubType,
                    ModuleId = module,
                    EventId = securityEvent.Id,
                    Time = securityEvent.Time,
                    PersonId = securityEvent.PersonId
                };
                var hasImage = report.ImageUrl != null;
                if (hasImage && Options.DownloadSnapshots && _memoryStorageService != null)
                    report.ImageUrl = await KeepSnapshotAsync(report.ImageUrl!, template.Id, cancellationToken).ConfigureAwait(false);

                SendReport(this, new Report
                {
                    Id = template.Id,
                    Value = new ValueModel(report),
                    TimeStamp = securityEvent.Time ?? Now().ToUnixTimeSeconds(),
                    Filter = hasImage ? "image" : "no-image"
                });
            }
            finally
            {
                _eventGate.Release();
            }
        }

        /// <summary>What a security event is; null for one that isn't reported as an event.</summary>
        internal SecurityEventType? Classify(NetatmoSecurityEvent securityEvent) => securityEvent.Type.ToLowerInvariant() switch
        {
            "movement" or "outdoor" => SecurityEventType.motionDetected,
            "person" when securityEvent.IsKnown == false => SecurityEventType.strangerPersonDetected,
            "person" when securityEvent.PersonId != null && PersonName(securityEvent.PersonId) != null => SecurityEventType.personName,
            // A face without a name in the home is unknown, whether pushed (is_known false) or polled.
            "person" when securityEvent.PersonId != null && securityEvent.IsKnown != true => SecurityEventType.strangerPersonDetected,
            "person" or "human" => SecurityEventType.personDetected,
            "animal" => SecurityEventType.animalDetected,
            "vehicle" => SecurityEventType.vehicleDetected,
            "alarm_started" => SecurityEventType.soundDetected,
            "incoming_call" => SecurityEventType.doorbellPressed,
            "accepted_call" => SecurityEventType.doorbellCallAccepted,
            "missed_call" => SecurityEventType.doorbellCallMissed,
            "tag_open" => SecurityEventType.doorOpened,
            "tag_big_move" or "tag_small_move" => SecurityEventType.tagMoved,
            "tag_uninstalled" or "tampered" or "siren_tampered" => SecurityEventType.tampered,
            "smoke" => SecurityEventType.smokeDetected,
            "co_detected" => SecurityEventType.coDetected,
            "siren_sounding" => SecurityEventType.sirenSounding,
            "disconnection" => SecurityEventType.connectionLost,
            _ => null
        };

        // A person event's key leaves out how it was classified, so its pushed and polled copies match.
        private static string KeyOf(NetatmoSecurityEvent securityEvent, SecurityEventType category) =>
            securityEvent.DeduplicationKey(securityEvent.Type.Equals("person", StringComparison.OrdinalIgnoreCase) ? "person" : category.ToString());

        private string? PersonName(string? personId) =>
            personId == null ? null : _home?.persons?.FirstOrDefault(p => p.id == personId)?.pseudo;

        private string ModuleName(string? moduleId) =>
            _home?.modules?.FirstOrDefault(m => m.id == moduleId)?.name ?? moduleId ?? "Netatmo";

        /// <summary>Keeps the snapshot in memory storage and returns its URL there; Netatmo's URL when the download fails.</summary>
        private async Task<string> KeepSnapshotAsync(string url, string reportId, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning("Could not download a Netatmo snapshot: {Status}.", response.StatusCode);
                    return url;
                }

                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return _memoryStorageService.Save(new Document
                {
                    Data = content,
                    Epochmt = DateTime.UtcNow.ToEpoch(),
                    Filename = Guid.NewGuid().ToString(),
                    Isfolder = FileOrFolder.File,
                    Filetype = DocumentType.Photo,
                    Filesize = content.Length.ToString(),
                    Properties = new Dictionary<string, string>()
                }, reportId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // The event is reported anyway, with Netatmo's URL.
                Logger.LogWarning("Could not download a Netatmo snapshot: {Error}", error.Message);
                return url;
            }
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            if (command.Address == "refresh")
            {
                _lastPoll = default;
                await CallNetatmoAsync(PollOrLoadAsync, cancellationToken).ConfigureAwait(false);
                return;
            }

            var home = _home ?? throw new DeviceNotReadyException("The Netatmo home isn't loaded yet.");
            var (target, setting) = Split(command.Address);

            if (command.Address == "home|away")
            {
                await CommandAsync(Api.PostFormAsync<NetatmoCommandResult>("api/setpersonsaway", [KeyValuePair.Create("home_id", home.id)], cancellationToken), "set everyone away").ConfigureAwait(false);
                foreach (var person in home.persons ?? [])
                    SendState($"person|{person.id}", false);
                return;
            }

            if (target == "person")
            {
                var personId = setting;
                var atHome = ParseBoolean(value);
                var form = atHome
                    ? new[] { KeyValuePair.Create("home_id", home.id), KeyValuePair.Create("person_ids[]", personId) }
                    : new[] { KeyValuePair.Create("home_id", home.id), KeyValuePair.Create("person_id", personId) };
                await CommandAsync(Api.PostFormAsync<NetatmoCommandResult>(atHome ? "api/setpersonshome" : "api/setpersonsaway", form, cancellationToken),
                    atHome ? "set a person home" : "set a person away").ConfigureAwait(false);
                SendState(command.Address, atHome);
                return;
            }

            switch (setting)
            {
                case "monitoring":
                    var on = ParseBoolean(value);
                    await SetStateAsync(home.id, target, "monitoring", on ? "on" : "off", cancellationToken).ConfigureAwait(false);
                    SendState(command.Address, on);
                    return;
                case "floodlight":
                    var mode = ParseText(value).ToLowerInvariant();
                    if (mode is not ("on" or "off" or "auto"))
                        throw new ArgumentException($"The floodlight mode must be on, off or auto, not '{mode}'.");
                    await SetStateAsync(home.id, target, "floodlight", mode, cancellationToken).ConfigureAwait(false);
                    SendState(command.Address, mode);
                    return;
                default:
                    Logger.LogWarning("NetatmoSecurity has no command at address {Address}.", command.Address);
                    return;
            }
        }

        private Task SetStateAsync(string homeId, string moduleId, string setting, string value, CancellationToken cancellationToken)
        {
            var module = new Dictionary<string, object> { ["id"] = moduleId, [setting] = value };
            var body = new { home = new { id = homeId, modules = new[] { module } } };
            return CommandAsync(Api.PostJsonAsync<NetatmoCommandResult>("api/setstate", body, cancellationToken), $"set {setting} of {moduleId}");
        }

        private static async Task CommandAsync(Task<NetatmoCommandResult?> call, string what)
        {
            var result = await call.ConfigureAwait(false);
            if (result?.status != null && !string.Equals(result.status, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Netatmo didn't {what}: {result.status}.");
        }

        private static (string Target, string Setting) Split(string address)
        {
            var bar = address.IndexOf('|');
            return bar < 0 ? (address, "") : (address[..bar], address[(bar + 1)..]);
        }

        internal static string ParseText(string value)
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.StartsWith('"'))
            {
                try { return JsonSerializer.Deserialize<string>(trimmed) ?? ""; }
                catch (JsonException) { }
            }
            return trimmed;
        }

        internal static bool ParseBoolean(string value) => ParseText(value).ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "yes" => true,
            "false" or "0" or "off" or "no" => false,
            var other => throw new ArgumentException($"Expected true or false, not '{other}'.")
        };

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Netatmo Security",
                ClassFullName = GetType().FullName!,
                RefreshSchedule = "0 0/5 * ? * * *",
                DeviceParameters = new Dictionary<string, string>
                {
                    { "clientId", "" },
                    { "clientSecret", "" },
                    { "token", "" },
                    { "refresh_token", "" },
                    { "homeId", "" },
                    { "webhookUrl", "" },
                    { "webhookSecret", "" },
                    { "webhookTakeover", "false" },
                    { "downloadSnapshots", "true" }
                },
                ReportTemplates = [],
                CommandTemplates = []
            };

            // Built from what the last start or refresh read: templates never call Netatmo.
            var home = _home;
            if (State != DeviceState.Running || home == null)
                return deviceConfiguration;

            var reports = deviceConfiguration.ReportTemplates;
            var commands = deviceConfiguration.CommandTemplates;
            commands.Add(Command("refresh", "Refresh", Core.ValueType.Boolean, true));

            foreach (var module in home.modules ?? [])
            {
                if (!SecurityTypes.Contains(module.type))
                    continue;
                var name = module.name ?? module.id;

                reports.Add(new ReportTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = $"{module.id}|event",
                    Name = $"{name} ({module.type})",
                    Type = Core.ValueType.Entity,
                    Model = new ValueModel(new SecurityReport
                    {
                        ImageUrl = "http://url-to-image",
                        SecurityEvent = SecurityEventType.motionDetected,
                        Message = "msg",
                        Source = "netatmo",
                        EventValue = "",
                        ModuleId = module.id
                    }),
                    Filters = ["image", "no-image"]
                });

                if (CameraTypes.Contains(module.type))
                {
                    reports.Add(Report($"{module.id}|monitoring", $"{name} monitoring", Core.ValueType.Boolean, true));
                    commands.Add(Command($"{module.id}|monitoring", $"{name} monitoring", Core.ValueType.Boolean, true));
                }
                if (ConnectedTypes.Contains(module.type))
                    reports.Add(Report($"{module.id}|connection", $"{name} connected", Core.ValueType.Boolean, true));
                if (string.Equals(module.type, "NOC", StringComparison.OrdinalIgnoreCase))
                {
                    reports.Add(Report($"{module.id}|floodlight", $"{name} floodlight", Core.ValueType.Text, "auto"));
                    commands.Add(Command($"{module.id}|floodlight", $"{name} floodlight", Core.ValueType.Text, "auto"));
                }
                if (BatteryTypes.Contains(module.type))
                    reports.Add(Report($"{module.id}|battery", $"{name} battery", Core.ValueType.Text, "full"));
            }

            foreach (var person in home.persons ?? [])
            {
                if (string.IsNullOrEmpty(person.pseudo))
                    continue;
                reports.Add(Report($"person|{person.id}", $"{person.pseudo} at home", Core.ValueType.Boolean, true));
                commands.Add(Command($"person|{person.id}", $"{person.pseudo} at home", Core.ValueType.Boolean, true));
            }

            commands.Add(Command("home|away", "Everyone away", Core.ValueType.Boolean, true));
            reports.Add(Report("home|webhook", "Webhook active", Core.ValueType.Boolean, true));
            return deviceConfiguration;
        }

        private static ReportTemplate Report(string address, string name, Core.ValueType type, object model) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = address,
            Name = name,
            Type = type,
            Model = model
        };

        private static CommandTemplate Command(string address, string name, Core.ValueType type, object model) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = address,
            Name = name,
            Type = type,
            Model = model
        };

        /// <summary>The keys of recent events, so one pushed and then polled, or pushed twice, is reported once.</summary>
        private sealed class RecentKeys(int capacity)
        {
            private readonly object _lock = new();
            private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
            private readonly Queue<string> _order = new();

            public bool Add(string key)
            {
                lock (_lock)
                {
                    if (!_keys.Add(key))
                        return false;
                    _order.Enqueue(key);
                    while (_order.Count > capacity)
                        _keys.Remove(_order.Dequeue());
                    return true;
                }
            }

            public void Clear()
            {
                lock (_lock)
                {
                    _keys.Clear();
                    _order.Clear();
                }
            }
        }
    }
}
