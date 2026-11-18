using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Services.EspHome;
using RIoT2.Net.Devices.Services.Voice;
using static RIoT2.Net.Devices.Services.EspHome.EspHomeApi;

namespace RIoT2.Net.Devices.Catalog
{
    public partial class VoiceSatellite
    {
        /// <summary>The longest timer.</summary>
        internal const int MaxTimerSeconds = 24 * 3600;

        /// <summary>The most timers at a time.</summary>
        internal const int MaxTimers = 20;

        private const string ClientInfo = "RIoT2 voice satellite";
        private const int MaxTimerName = 60;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

        private readonly Dictionary<string, VoiceTimer> _timers = new(StringComparer.Ordinal);

        /// <summary>Where the device notes that a satellite took its key; tests use a directory of their own.</summary>
        internal string DataDirectory { get; set; } = "Data";

        // The ESPHome connection, encrypted when there is a key. Null when the satellite had no key and was just given
        // it: the caller connects again, with it.
        private async Task<Session?> OpenEspHomeAsync(CancellationToken stop)
        {
            if (_key == null)
                return new EspHomeSession(this, await OpenEspHomeConnectionAsync(null, stop).ConfigureAwait(false), stop);
            try
            {
                var connection = await OpenEspHomeConnectionAsync(_key, stop).ConfigureAwait(false);
                NoteKeyTaken();
                return new EspHomeSession(this, connection, stop);
            }
            catch (EspHomeKeyException) when (!KeyTaken())
            {
            }
            catch (EspHomeKeyException)
            {
                // The all-zero key doesn't authenticate the satellite: whatever answers at its address now could take
                // the key. So it's given only once, as Home Assistant gives one only when it adopts a device.
                throw new InvalidOperationException("The satellite refused the encryption key it took before. If it was reset, set a new " +
                    "encryptionKey to give it one again.");
            }

            // A satellite without a key takes only the all-zero one, and then a key over that encrypted link: as Home
            // Assistant gives one, so that the key never crosses the network readable.
            EspHomeConnection provisioning;
            try
            {
                provisioning = await OpenEspHomeConnectionAsync(new byte[NoiseHandshake.KeyLength], stop).ConfigureAwait(false);
            }
            catch (EspHomeKeyException)
            {
                throw new InvalidOperationException("The satellite has another encryption key, for example from Home Assistant: set that key as the " +
                    "encryptionKey, or remove the satellite from Home Assistant (or delete hassmic's state/api_key) so that this device can give it its own.");
            }
            await using (provisioning.ConfigureAwait(false))
                await ProvisionAsync(provisioning, stop).ConfigureAwait(false);
            Logger.LogInformation("Gave the voice satellite at {Host}:{Port} its encryption key.", _host, _port);
            return null;
        }

        // A file per satellite and key, named by their hash: the key itself is never written.
        private string KeyTakenPath()
        {
            var hash = SHA256.HashData([.. Encoding.UTF8.GetBytes($"{_host.ToLowerInvariant()}:{_port}:"), .. _key!]);
            return Path.Combine(DataDirectory, $"voice-satellite-{Convert.ToHexStringLower(hash)[..16]}.keyed");
        }

        private bool KeyTaken()
        {
            try { return File.Exists(KeyTakenPath()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }

        private void NoteKeyTaken()
        {
            var path = KeyTakenPath();
            try
            {
                if (File.Exists(path))
                    return;
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning("Could not note that the voice satellite took its key in {Path}: {Reason}", path, error.Message);
            }
        }

        private async Task<EspHomeConnection> OpenEspHomeConnectionAsync(byte[]? key, CancellationToken stop)
        {
            var client = await OpenTcpAsync(_host, _port, stop).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(HandshakeTimeout);
            try
            {
                return await EspHomeConnection.OpenAsync(client.GetStream(), client, key, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                throw new TimeoutException($"The satellite didn't finish the encryption handshake in {HandshakeTimeout.TotalSeconds} s.");
            }
        }

        private async Task ProvisionAsync(EspHomeConnection connection, CancellationToken stop)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(HandshakeTimeout);
            try
            {
                await connection.WriteAsync(HelloRequest, Hello(ClientInfo), timeout.Token).ConfigureAwait(false);
                await connection.WriteAsync(NoiseEncryptionSetKeyRequest, new ProtoWriter().Bytes(1, _key), timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var message = await connection.ReadAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new IOException("The satellite closed the connection before it took the encryption key.");
                    if (message.Type != NoiseEncryptionSetKeyResponse)
                        continue;
                    if (!EspHomeSession.ReadBool(message.Payload, 1))
                        throw new InvalidOperationException("The satellite refused the encryption key.");
                    return;
                }
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                throw new TimeoutException($"The satellite didn't take the encryption key in {HandshakeTimeout.TotalSeconds} s.");
            }
        }

        private async Task TimerCommandAsync(JsonElement value, CancellationToken cancellationToken)
        {
            string? name = null;
            var cancel = false;
            double? seconds = null;
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        switch (property.Name.ToLowerInvariant())
                        {
                            case "name":
                                name = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : throw new ArgumentException("The timer's name must be text.", nameof(value));
                                break;
                            case "cancel":
                                cancel = property.Value.ValueKind == JsonValueKind.True;
                                break;
                            case "seconds":
                                seconds = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetDouble() : throw new ArgumentException("The timer's seconds must be a number.", nameof(value));
                                break;
                        }
                    }
                    break;
                case JsonValueKind.Number:
                    seconds = value.GetDouble();
                    break;
                case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    seconds = parsed;
                    break;
                default:
                    throw new ArgumentException("The timer command takes {seconds, name} or {cancel: true, name}.", nameof(value));
            }

            name = new string((name ?? "").Where(x => !char.IsControl(x)).ToArray()).Trim();
            if (name.Length > MaxTimerName)
                throw new ArgumentException($"The timer's name is longer than {MaxTimerName} characters.", nameof(value));
            if (cancel)
            {
                await CancelTimersAsync(name.Length == 0 ? null : name).ConfigureAwait(false);
                return;
            }
            if (seconds is not double length || double.IsNaN(length) || length < 1 || length > MaxTimerSeconds)
                throw new ArgumentException($"The timer's seconds must be 1-{MaxTimerSeconds}.", nameof(value));

            VoiceTimer timer;
            lock (_gate)
            {
                if (_timers.Count >= MaxTimers)
                    throw new InvalidOperationException($"{MaxTimers} timers run already.");
                var whole = (uint)Math.Round(length);
                timer = new VoiceTimer(Guid.NewGuid().ToString("N"), name, whole, DateTimeOffset.UtcNow.AddSeconds(whole));
                _timers[timer.Id] = timer;
            }
            _ = Task.Run(() => RunTimerAsync(timer), CancellationToken.None);
            if (!await SendTimerAsync(TimerEvent.Started, timer).ConfigureAwait(false))
                Logger.LogInformation("The timer {Name} runs; the voice satellite hears of it when it's connected again.", timer.Name);
        }

        private async Task RunTimerAsync(VoiceTimer timer)
        {
            try
            {
                var left = timer.Ends - DateTimeOffset.UtcNow;
                if (left > TimeSpan.Zero)
                    await Task.Delay(left, timer.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_gate)
            {
                if (!_timers.Remove(timer.Id))
                    return;
            }
            if (!await SendTimerAsync(TimerEvent.Finished, timer).ConfigureAwait(false))
                Logger.LogWarning("The timer {Name} ended while the voice satellite was offline.", timer.Name);
        }

        private async Task CancelTimersAsync(string? name)
        {
            List<VoiceTimer> cancelled;
            lock (_gate)
            {
                cancelled = _timers.Values.Where(x => name == null || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var timer in cancelled)
                {
                    _timers.Remove(timer.Id);
                    timer.Cancel.Cancel();
                }
            }
            if (name != null && cancelled.Count == 0)
                throw new ArgumentException($"No timer is named {name}.");
            foreach (var timer in cancelled)
                await SendTimerAsync(TimerEvent.Cancelled, timer).ConfigureAwait(false);
        }

        // The node stops: its timers go, without ringing.
        private void CancelAllTimers()
        {
            lock (_gate)
            {
                foreach (var timer in _timers.Values)
                    timer.Cancel.Cancel();
                _timers.Clear();
            }
        }

        // A new connection: the satellite hears of the timers that run.
        private async Task ResendTimersAsync()
        {
            List<VoiceTimer> running;
            lock (_gate)
                running = _timers.Values.ToList();
            foreach (var timer in running)
                await SendTimerAsync(TimerEvent.Started, timer).ConfigureAwait(false);
        }

        // False when no satellite is connected to hear of it.
        private async Task<bool> SendTimerAsync(TimerEvent type, VoiceTimer timer)
        {
            EspHomeSession? session;
            lock (_gate)
                session = _session as EspHomeSession;
            if (session == null)
                return false;
            var left = type is TimerEvent.Started or TimerEvent.Updated
                ? (uint)Math.Max(0, Math.Ceiling((timer.Ends - DateTimeOffset.UtcNow).TotalSeconds))
                : 0;
            try
            {
                await session.SendTimerAsync(type, timer, left).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                Logger.LogDebug("Could not tell the voice satellite of the timer {Name}: {Reason}", timer.Name, error.Message);
                return false;
            }
        }

        private sealed class VoiceTimer(string id, string name, uint seconds, DateTimeOffset ends)
        {
            public string Id { get; } = id;
            public string Name { get; } = name;
            public uint Seconds { get; } = seconds;
            public DateTimeOffset Ends { get; } = ends;
            public CancellationTokenSource Cancel { get; } = new();
        }

        /// <summary>What a <c>light</c> command sets on the light ring; what it doesn't name stays.</summary>
        internal sealed record LightChange(bool? On, int? Brightness, (byte R, byte G, byte B)? Color, string? Effect)
        {
            public static LightChange Parse(JsonElement value)
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return new LightChange(value.ValueKind == JsonValueKind.True, null, null, null);
                    case JsonValueKind.String:
                        var text = value.GetString()!.Trim().ToLowerInvariant();
                        if (text is "on" or "true")
                            return new LightChange(true, null, null, null);
                        if (text is "off" or "false")
                            return new LightChange(false, null, null, null);
                        return new LightChange(true, null, ParseColor(text), null);
                    case JsonValueKind.Object:
                        bool? on = null;
                        int? brightness = null;
                        (byte, byte, byte)? color = null;
                        string? effect = null;
                        foreach (var property in value.EnumerateObject())
                        {
                            var item = property.Value;
                            switch (property.Name.ToLowerInvariant())
                            {
                                case "on":
                                    on = item.ValueKind switch
                                    {
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => throw new ArgumentException("The light's on must be true or false.", nameof(value))
                                    };
                                    break;
                                case "brightness":
                                    if (item.ValueKind != JsonValueKind.Number || item.GetDouble() is var b && (double.IsNaN(b) || b < 0 || b > 100))
                                        throw new ArgumentException("The light's brightness must be 0-100.", nameof(value));
                                    brightness = (int)Math.Round(item.GetDouble());
                                    break;
                                case "color":
                                    color = item.ValueKind == JsonValueKind.String ? ParseColor(item.GetString()!) : throw new ArgumentException("The light's color must be #rrggbb.", nameof(value));
                                    break;
                                case "effect":
                                    effect = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : throw new ArgumentException("The light's effect must be text.", nameof(value));
                                    if (effect.Length is 0 or > 32 || effect.Any(char.IsControl))
                                        throw new ArgumentException("The light's effect must be an effect's name.", nameof(value));
                                    break;
                            }
                        }
                        if (on == null && brightness == null && color == null && effect == null)
                            throw new ArgumentException("The light command names nothing to set: on, brightness, color or effect.", nameof(value));
                        // Setting a colour, brightness or effect turns the light on, unless the command says otherwise.
                        on ??= brightness is > 0 || color != null || effect != null ? true : null;
                        return new LightChange(on, brightness, color, effect);
                    default:
                        throw new ArgumentException("The light command takes true, false or {on, brightness, color, effect}.", nameof(value));
                }
            }

            private static (byte, byte, byte) ParseColor(string text)
            {
                var hex = text.Trim().TrimStart('#');
                if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
                    throw new ArgumentException("The light's color must be #rrggbb.");
                return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
        }

        /// <summary>
        /// The ESPHome native API, as hassmic speaks it by default: the node is the client, as Home Assistant is. It lists
        /// the satellite's entities and subscribes to their states and to the voice assistant, whose microphone audio
        /// comes over this connection. The answer is fetched by the satellite from the node over HTTP; announcements too.
        /// </summary>
        private sealed class EspHomeSession(VoiceSatellite device, EspHomeConnection connection, CancellationToken stop) : Session(stop)
        {
            // hassmic's entities, by object id.
            public const string MuteSwitch = "mute";
            public const string DoNotDisturbSwitch = "do_not_disturb";
            public const string StopRingingButton = "stop_ringing";
            public const string ButtonEvents = "button";
            public const string TimerRingingSensor = "timer_ringing";
            public const string LightRing = "light_ring";

            private readonly object _sync = new();
            private readonly Dictionary<string, Entity> _byObjectId = new(StringComparer.Ordinal);
            private readonly Dictionary<uint, Entity> _byKey = [];
            private readonly Dictionary<string, string> _reported = new(StringComparer.Ordinal);
            private List<(string Id, string Name)> _wakeWords = [];
            private bool _wakeWordsLogged;
            private uint? _mediaPlayer;
            private bool _answered;
            private bool _listed;
            private TaskCompletionSource? _played;
            private bool _announcing;
            private bool _sawPlaying;

            public override async Task StartAsync()
            {
                // Pipelined, as the satellite handles them in order. ConnectRequest has no password.
                await Write(HelloRequest, Hello(ClientInfo)).ConfigureAwait(false);
                await Write(ConnectRequest, null).ConfigureAwait(false);
                await Write(DeviceInfoRequest, null).ConfigureAwait(false);
                await Write(ListEntitiesRequest, null).ConfigureAwait(false);
            }

            public override async Task<bool> ReceiveAsync(CancellationToken cancellationToken)
            {
                var message = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (message == null)
                    return false;
                var payload = message.Payload;
                switch (message.Type)
                {
                    case HelloResponse:
                        var major = ReadUInt(payload, 1);
                        if (major != VersionMajor)
                            throw new EspHomeProtocolException($"The satellite speaks version {major} of the ESPHome API, not {VersionMajor}.");
                        if (!_answered)
                        {
                            _answered = true;
                            OnAnswered?.Invoke();
                        }
                        break;
                    case ConnectResponse:
                        if (ReadBool(payload, 1))
                            throw new InvalidOperationException("The satellite wants an API password, which this device doesn't support.");
                        break;
                    case DisconnectRequest:
                        await Write(DisconnectResponse, null).ConfigureAwait(false);
                        return false;
                    case PingRequest:
                        await Write(PingResponse, null).ConfigureAwait(false);
                        break;
                    case DeviceInfoResponse:
                        LogDeviceInfo(payload);
                        break;
                    case ListEntitiesMediaPlayer:
                    case ListEntitiesSwitch:
                    case ListEntitiesLight:
                    case ListEntitiesButton:
                    case ListEntitiesEvent:
                    case ListEntitiesBinarySensor:
                        AddEntity(message.Type, payload);
                        break;
                    case ListEntitiesDone:
                        await ListedAsync().ConfigureAwait(false);
                        break;
                    case MediaPlayerState:
                    case SwitchState:
                    case BinarySensorState:
                    case LightState:
                    case Event:
                        OnState(message.Type, payload);
                        break;
                    case GetTimeRequest:
                        await Write(GetTimeResponse, new ProtoWriter()
                            .Fixed32(1, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                            .String(2, PosixTimeZone.Local())).ConfigureAwait(false);
                        break;
                    case VoiceAssistantRequest:
                        await OnVoiceRequestAsync(payload).ConfigureAwait(false);
                        break;
                    case VoiceAssistantAudio:
                        var (audio, end) = ReadAudio(payload);
                        if (audio.Length > 0)
                            await device.AudioAsync(this, 16000, audio).ConfigureAwait(false);
                        if (end)
                            await device.AudioEndedAsync(this).ConfigureAwait(false);
                        break;
                    case VoiceAssistantAnnounceFinished:
                        var success = ReadBool(payload, 1);
                        lock (_sync)
                        {
                            if (_played != null && _announcing)
                            {
                                if (success)
                                    _played.TrySetResult();
                                else
                                    _played.TrySetException(new InvalidOperationException("The satellite didn't play the announcement: do not disturb is on, or it was playing something else."));
                            }
                        }
                        break;
                    case VoiceAssistantConfigurationResponse:
                        OnWakeWords(payload);
                        break;
                }
                return true;
            }

            // Every entity is listed: the states and the voice assistant are subscribed, and commands may use it.
            private async Task ListedAsync()
            {
                if (_listed)
                    return;
                _listed = true;
                await Write(SubscribeStates, null).ConfigureAwait(false);
                await Write(SubscribeVoiceAssistant, new ProtoWriter().Bool(1, true).UInt(2, SubscribeApiAudio)).ConfigureAwait(false);
                await Write(VoiceAssistantConfigurationRequest, null).ConfigureAwait(false);
                device.Connected(this, connection.Encrypted ? "ESPHome API, encrypted" : "ESPHome API");
                await device.ResendTimersAsync().ConfigureAwait(false);
            }

            private async Task OnVoiceRequestAsync(byte[] payload)
            {
                var start = false;
                uint flags = 0;
                ParseVoiceRequest(payload, ref start, ref flags);
                if (!start)
                {
                    device.AbortRun(this);
                    return;
                }
                if ((flags & RequestUseWakeWord) != 0)
                {
                    device.WarnWakeWordOnServer($"ESPHome flags {flags}");
                    await Write(VoiceAssistantResponse, new ProtoWriter().Bool(2, true)).ConfigureAwait(false);
                    return;
                }
                device.BeginRun(this);
                // Port 0: the audio comes over this connection.
                await Write(VoiceAssistantResponse, null).ConfigureAwait(false);
                await WriteEvent(VoiceEvent.RunStart).ConfigureAwait(false);
                await WriteEvent(VoiceEvent.SttStart).ConfigureAwait(false);
            }

            public override Task PingAsync() => Write(PingRequest, null);

            public override Task SpeechEndedAsync() => WriteEvent(VoiceEvent.SttVadEnd);

            public override async Task TranscriptAsync(string text)
            {
                await WriteEvent(VoiceEvent.SttEnd, ("text", text)).ConfigureAwait(false);
                await WriteEvent(VoiceEvent.IntentStart).ConfigureAwait(false);
            }

            public override async Task EndRunAsync(string code, string text)
            {
                await WriteEvent(VoiceEvent.Error, ("code", code), ("message", text)).ConfigureAwait(false);
                await WriteEvent(VoiceEvent.RunEnd).ConfigureAwait(false);
            }

            // The satellite fetches the speech from the node: a reply ends the request with its URL, an announcement is
            // asked for. A reply is played when the media player goes from playing back to idle.
            public override async Task PlayAsync(PcmAudio audio, string text, bool reply, CancellationToken cancellationToken)
            {
                var url = device.AudioUrl(audio);
                var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync)
                {
                    _played = played;
                    _announcing = !reply;
                    _sawPlaying = false;
                }
                try
                {
                    if (reply)
                    {
                        await WriteEvent(VoiceEvent.IntentEnd).ConfigureAwait(false);
                        await WriteEvent(VoiceEvent.TtsStart, ("text", text)).ConfigureAwait(false);
                        await WriteEvent(VoiceEvent.TtsEnd, ("url", url)).ConfigureAwait(false);
                        await WriteEvent(VoiceEvent.RunEnd).ConfigureAwait(false);
                    }
                    else
                    {
                        await Write(VoiceAssistantAnnounce, new ProtoWriter().String(1, url).String(2, text)).ConfigureAwait(false);
                    }
                    await device.WaitPlayedAsync(this, played.Task, audio, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_played == played)
                            _played = null;
                    }
                }
            }

            public Task SendTimerAsync(TimerEvent type, VoiceTimer timer, uint secondsLeft) =>
                Write(VoiceAssistantTimerEvent, TimerEventMessage(type, timer.Id, timer.Name, timer.Seconds, secondsLeft,
                    type is TimerEvent.Started or TimerEvent.Updated));

            public Task SetVolumeAsync(int percent, CancellationToken cancellationToken)
            {
                uint key;
                lock (_sync)
                    key = _mediaPlayer ?? throw new InvalidOperationException("The satellite lists no media player.");
                return Write(MediaPlayerCommand, new ProtoWriter().Fixed32(1, key).Bool(4, true).Float(5, percent / 100f));
            }

            public Task SetSwitchAsync(string objectId, bool on, CancellationToken cancellationToken) =>
                Write(SwitchCommand, new ProtoWriter().Fixed32(1, Find(objectId, ListEntitiesSwitch).Key).Bool(2, on));

            public Task PressButtonAsync(string objectId, CancellationToken cancellationToken) =>
                Write(ButtonCommand, new ProtoWriter().Fixed32(1, Find(objectId, ListEntitiesButton).Key));

            public Task SetLightAsync(LightChange change, CancellationToken cancellationToken)
            {
                var light = Find(LightRing, ListEntitiesLight);
                var effect = change.Effect;
                if (effect != null)
                {
                    // Home Assistant's name for no effect is "off"; "None" is the UI's.
                    if (effect.Equals("none", StringComparison.OrdinalIgnoreCase) || effect.Equals("off", StringComparison.OrdinalIgnoreCase))
                        effect = "off";
                    else
                        effect = light.Effects.FirstOrDefault(x => x.Equals(effect, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"The light ring has no effect {effect}; it has {string.Join(", ", light.Effects)}.");
                }
                var command = new ProtoWriter().Fixed32(1, light.Key);
                if (change.On is bool on)
                    command.Bool(2, true).Bool(3, on);
                if (change.Brightness is int brightness)
                    command.Bool(4, true).Float(5, brightness / 100f);
                if (change.Color is { } color)
                    command.Bool(6, true).Float(7, color.R / 255f).Float(8, color.G / 255f).Float(9, color.B / 255f);
                if (effect != null)
                    command.Bool(18, true).String(19, effect);
                return Write(LightCommand, command);
            }

            public async Task SetWakeWordAsync(string wanted, CancellationToken cancellationToken)
            {
                List<(string Id, string Name)> words;
                lock (_sync)
                    words = _wakeWords;
                if (words.Count == 0)
                    throw new InvalidOperationException("The satellite lists no wake words to choose from.");
                var word = words.FirstOrDefault(x => x.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (word.Id == null)
                    word = words.FirstOrDefault(x => x.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                if (word.Id == null)
                    throw new ArgumentException($"The satellite has no wake word {wanted}; it has {string.Join(", ", words.Select(x => x.Id))}.");
                await Write(VoiceAssistantSetConfiguration, new ProtoWriter().String(1, word.Id)).ConfigureAwait(false);
                // Its answer reports the wake word in use.
                await Write(VoiceAssistantConfigurationRequest, null).ConfigureAwait(false);
            }

            public override ValueTask CloseAsync() => connection.DisposeAsync();

            // Written on the connection's token: a cancelled command doesn't cut a frame off.
            private Task Write(int type, ProtoWriter? message) => connection.WriteAsync(type, message, Token);

            private Task WriteEvent(VoiceEvent type, params (string Name, string Value)[] data) =>
                Write(VoiceAssistantEvent, VoiceAssistantEventMessage(type, data));

            private Entity Find(string objectId, int kind)
            {
                lock (_sync)
                {
                    if (_byObjectId.TryGetValue(objectId, out var entity) && entity.Kind == kind)
                        return entity;
                }
                throw new InvalidOperationException(objectId == LightRing
                    ? "The satellite lists no light ring: switch on a light ring of its own on hassmic's settings page."
                    : $"The satellite lists no {objectId} entity.");
            }

            private void LogDeviceInfo(byte[] payload)
            {
                string name = "", project = "", version = "";
                uint features = 0;
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    switch (field.Number)
                    {
                        case 2: name = field.String; break;
                        case 8: project = field.String; break;
                        case 9: version = field.String; break;
                        case 17: features = field.UInt32; break;
                    }
                }
                device.Logger.LogInformation("The voice satellite at {Host} is {Name} ({Project} {Version}).", device._host, name, project, version);
                if ((features & 1) == 0)
                    device.Logger.LogWarning("The satellite at {Host} lists no voice assistant.", device._host);
            }

            private void AddEntity(int kind, byte[] payload)
            {
                string objectId = "", name = "";
                uint key = 0;
                var effects = new List<string>();
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    if (field.Number == 1)
                        objectId = field.String;
                    else if (field.Number == 2)
                        key = field.Fixed32;
                    else if (field.Number == 3)
                        name = field.String;
                    else if (field.Number == 11 && kind == ListEntitiesLight)
                        effects.Add(field.String);
                }
                lock (_sync)
                {
                    var entity = new Entity(kind, key, objectId, name, [.. effects]);
                    _byObjectId[objectId] = entity;
                    _byKey[key] = entity;
                    if (kind == ListEntitiesMediaPlayer)
                        _mediaPlayer ??= key;
                }
            }

            private void OnState(int type, byte[] payload)
            {
                uint key = 0, state = 0;
                bool on = false, missing = false;
                float volume = 0, brightness = 0, red = 0, green = 0, blue = 0;
                string text = "";
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    switch (type, field.Number)
                    {
                        case (_, 1): key = field.Fixed32; break;
                        case (MediaPlayerState, 2): state = field.UInt32; break;
                        case (MediaPlayerState, 3): volume = field.Float; break;
                        case (SwitchState or BinarySensorState or LightState, 2): on = field.Bool; break;
                        case (BinarySensorState, 3): missing = field.Bool; break;
                        case (LightState, 3): brightness = field.Float; break;
                        case (LightState, 4): red = field.Float; break;
                        case (LightState, 5): green = field.Float; break;
                        case (LightState, 6): blue = field.Float; break;
                        case (LightState, 9): text = field.String; break;
                        case (Event, 2): text = field.String; break;
                    }
                }

                Entity? entity;
                lock (_sync)
                {
                    _byKey.TryGetValue(key, out entity);
                    if (type == MediaPlayerState && key == _mediaPlayer && _played != null && !_announcing)
                    {
                        if (state is MediaPlayerPlaying or MediaPlayerAnnouncing)
                            _sawPlaying = true;
                        else if (state == MediaPlayerIdle && _sawPlaying)
                            _played.TrySetResult();
                    }
                }
                if (entity == null)
                    return;

                switch (type, entity.ObjectId)
                {
                    case (MediaPlayerState, _):
                        ReportChanged(VolumeAddress, (int)Math.Round(Math.Clamp(volume, 0, 1) * 100));
                        break;
                    case (SwitchState, MuteSwitch):
                        ReportChanged(MuteAddress, on);
                        break;
                    case (SwitchState, DoNotDisturbSwitch):
                        ReportChanged(DoNotDisturbAddress, on);
                        break;
                    case (BinarySensorState, TimerRingingSensor) when !missing:
                        ReportChanged(TimerRingingAddress, on);
                        break;
                    case (LightState, LightRing):
                        ReportChanged(LightAddress, new Dictionary<string, object>
                        {
                            ["on"] = on,
                            ["brightness"] = (int)Math.Round(Math.Clamp(brightness, 0, 1) * 100),
                            ["color"] = $"#{Byte(red):x2}{Byte(green):x2}{Byte(blue):x2}",
                            ["effect"] = text.Length == 0 || text.Equals("off", StringComparison.OrdinalIgnoreCase) ? "None" : text
                        });
                        break;
                    case (Event, ButtonEvents) when text.Length > 0:
                        // Each press is news, the same as the last one too.
                        device.Report(ButtonAddress, JsonSerializer.SerializeToElement(text));
                        break;
                }
            }

            private static byte Byte(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

            // A state the satellite sent again unchanged (it sends all of them on each change of settings) isn't news.
            private void ReportChanged(string address, object value)
            {
                var json = JsonSerializer.SerializeToElement(value);
                var raw = json.GetRawText();
                if (_reported.TryGetValue(address, out var last) && last == raw)
                    return;
                _reported[address] = raw;
                device.Report(address, json);
            }

            private void OnWakeWords(byte[] payload)
            {
                var words = new List<(string Id, string Name)>();
                var active = new List<string>();
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    if (field.Number == 1 && field.Type == WireType.LengthDelimited)
                        words.Add(ReadWakeWord(field.Data));
                    else if (field.Number == 2)
                        active.Add(field.String);
                }
                lock (_sync)
                    _wakeWords = words;
                if (words.Count == 0)
                    return;
                if (!_wakeWordsLogged)
                {
                    _wakeWordsLogged = true;
                    device.Logger.LogInformation("The voice satellite's wake words: {WakeWords}.", string.Join(", ", words.Select(x => $"{x.Id} ({x.Name})")));
                }
                ReportChanged(WakeWordAddress, active.FirstOrDefault() ?? "");
            }

            private static (string Id, string Name) ReadWakeWord(ReadOnlySpan<byte> data)
            {
                string id = "", name = "";
                var reader = new ProtoReader(data);
                while (reader.Next(out var field))
                {
                    if (field.Number == 1)
                        id = field.String;
                    else if (field.Number == 2)
                        name = field.String;
                }
                return (id, name);
            }

            private static void ParseVoiceRequest(byte[] payload, ref bool start, ref uint flags)
            {
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    if (field.Number == 1)
                        start = field.Bool;
                    else if (field.Number == 3)
                        flags = field.UInt32;
                }
            }

            private static (byte[] Audio, bool End) ReadAudio(byte[] payload)
            {
                byte[] audio = [];
                var end = false;
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    if (field.Number == 1 && field.Type == WireType.LengthDelimited)
                        audio = field.Data.ToArray();
                    else if (field.Number == 2)
                        end = field.Bool;
                }
                return (audio, end);
            }

            internal static bool ReadBool(byte[] payload, int number) => ReadUInt(payload, number) != 0;

            private static uint ReadUInt(byte[] payload, int number)
            {
                uint value = 0;
                var reader = new ProtoReader(payload);
                while (reader.Next(out var field))
                {
                    if (field.Number == number && field.Type == WireType.Varint)
                        value = field.UInt32;
                }
                return value;
            }

            private sealed record Entity(int Kind, uint Key, string ObjectId, string Name, string[] Effects);
        }
    }
}
