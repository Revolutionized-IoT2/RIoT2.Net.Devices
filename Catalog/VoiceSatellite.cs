using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Interfaces;
using RIoT2.Net.Devices.Services.Voice;
using RIoT2.Net.Devices.Services.Wyoming;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="VoiceSatellite"/> device's parameters.</summary>
    public sealed class VoiceSatelliteOptions
    {
        public const string Wyoming = "wyoming";
        public const string EspHome = "esphome";
        public const string OpenAiCompatible = "openAiCompatible";
        public const string GoogleCloud = "googleCloud";
        public const string OpenAiUrl = "https://api.openai.com/v1";
        public const int WyomingPort = 16700;
        public const int EspHomePort = 26053;

        [DeviceParameter("host", Required = true, Description = "IP address or host name of the voice satellite, for example the Echo running hassmic")]
        public string Host { get; set; } = "";

        [DeviceParameter("protocol", Default = Wyoming, Description = "wyoming (hassmic with PROTO=wyoming) or esphome (hassmic's default, the ESPHome native API: also the light ring, buttons, volume, timers and announcements)")]
        public string Protocol { get; set; } = Wyoming;

        [DeviceParameter("port", Description = "The satellite's port; empty: 16700 for wyoming, 26053 for esphome")]
        public int? Port { get; set; }

        [DeviceParameter("encryptionKey", Secret = true, Description = "esphome: the encryption key, 32 bytes in base64 (for example from openssl rand -base64 32); the device gives it to a satellite that has none. Empty: no encryption")]
        public string EncryptionKey { get; set; } = "";

        [DeviceParameter("nodeBaseUrl", Description = "esphome: URL the satellite fetches the speech from this node at, for example http://192.168.1.10:8081; empty: the node's RIOT2_NODE_URL")]
        public string NodeBaseUrl { get; set; } = "";

        [DeviceParameter("language", Default = "en-US", Description = "Language spoken to the satellite, for example en-US, en-GB or fi-FI")]
        public string Language { get; set; } = "en-US";

        [DeviceParameter("sttEngine", Default = Wyoming, Description = "Speech to text: wyoming (for example wyoming-faster-whisper) or openAiCompatible (OpenAI, or a local server with /audio/transcriptions)")]
        public string SttEngine { get; set; } = Wyoming;

        [DeviceParameter("sttUrl", Description = "wyoming: tcp://host:port, for example tcp://192.168.1.10:10300; openAiCompatible: the API's base URL; empty: " + OpenAiUrl)]
        public string SttUrl { get; set; } = "";

        [DeviceParameter("sttModel", Description = "openAiCompatible: the model; empty: whisper-1")]
        public string SttModel { get; set; } = "";

        [DeviceParameter("sttApiKey", Secret = true, Description = "openAiCompatible: the API key, if the server needs one")]
        public string SttApiKey { get; set; } = "";

        [DeviceParameter("ttsEngine", Default = Wyoming, Description = "Text to speech: wyoming (for example wyoming-piper), openAiCompatible (OpenAI, or a local server with /audio/speech) or googleCloud")]
        public string TtsEngine { get; set; } = Wyoming;

        [DeviceParameter("ttsUrl", Description = "wyoming: tcp://host:port, for example tcp://192.168.1.10:10200; openAiCompatible: the API's base URL; empty: " + OpenAiUrl)]
        public string TtsUrl { get; set; } = "";

        [DeviceParameter("ttsModel", Description = "openAiCompatible: the model; empty: tts-1")]
        public string TtsModel { get; set; } = "";

        [DeviceParameter("ttsVoice", Description = "The voice: a Piper voice such as fi_FI-harri-medium (wyoming; empty: the server's), an OpenAI voice (empty: alloy) or a Google voice such as fi-FI-Wavenet-B (empty: Google's default)")]
        public string TtsVoice { get; set; } = "";

        [DeviceParameter("ttsApiKey", Secret = true, Description = "openAiCompatible or googleCloud: the API key")]
        public string TtsApiKey { get; set; } = "";

        [DeviceParameter("replyTimeoutSeconds", Default = "30", Description = "How long the satellite waits for the speak command that answers a request, 5-300 s")]
        public int ReplyTimeoutSeconds { get; set; } = 30;

        [DeviceParameter("maxRequestSeconds", Default = "15", Description = "The longest request, 3-60 s")]
        public int MaxRequestSeconds { get; set; } = 15;

        [DeviceParameter("silenceMs", Default = "700", Description = "The pause that ends a request, 200-3000 ms")]
        public int SilenceMs { get; set; } = 700;

        [DeviceParameter("speechThresholdDb", Default = "-45", Description = "The level under which audio never counts as speech, -80 to -10 dBFS; lower it if requests end too soon, raise it if they don't end in a noisy room")]
        public double SpeechThresholdDb { get; set; } = -45;
    }

    /// <summary>
    /// A voice satellite, such as an Amazon Echo running echo-dot-assist's hassmic, over the Wyoming protocol or the
    /// ESPHome native API. The satellite hears its wake word; the node finds the end of the request, turns it into
    /// text and reports it as <c>transcript</c>. A workflow answers with the <c>speak</c> command, whose speech the
    /// satellite plays. The node keeps one connection to the satellite and connects again when it drops. Over the
    /// ESPHome API the satellite's volume, microphone mute, do not disturb, light ring, buttons, wake word and timers
    /// are reports and commands too.
    /// </summary>
    [RequiresHardware("Voice satellite on the network (Wyoming or the ESPHome native API)")]
    public partial class VoiceSatellite : AsyncDeviceBase<VoiceSatelliteOptions>, IAsyncCommandDevice, IDeviceWithConfiguration
    {
        public const string StateAddress = "state";
        public const string TranscriptAddress = "transcript";
        public const string SpeakAddress = "speak";
        public const string CancelAddress = "cancel";

        // The ESPHome API's: a report and a command share an address where both exist.
        public const string VolumeAddress = "volume";
        public const string MuteAddress = "mute";
        public const string DoNotDisturbAddress = "doNotDisturb";
        public const string LightAddress = "light";
        public const string WakeWordAddress = "wakeWord";
        public const string ButtonAddress = "button";
        public const string TimerRingingAddress = "timerRinging";
        public const string TimerAddress = "timer";
        public const string StopRingingAddress = "stopRinging";

        public const string Idle = "idle";
        public const string Listening = "listening";
        public const string Thinking = "thinking";
        public const string Speaking = "speaking";
        public const string Offline = "offline";

        /// <summary>The longest text <c>speak</c> takes.</summary>
        public const int MaxTextLength = 1000;

        // hassmic ends a run quietly (no error animation) on this code.
        internal const string NoTextCode = "stt-no-text-recognized";

        /// <summary>How often the node pings the satellite.</summary>
        internal TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>A connection without any message for this long is dead.</summary>
        internal TimeSpan DeadAfter { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>The waits before connecting again, the last one repeated.</summary>
        internal TimeSpan[] ReconnectDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

        /// <summary>The longest wait for speech to text or text to speech.</summary>
        internal TimeSpan EngineTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long after its end a reply may play before the satellite says it was played.</summary>
        internal TimeSpan PlayedGrace { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How long a connection must last before the waits to connect again start over.</summary>
        internal TimeSpan StableAfter { get; set; } = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

        private readonly object _gate = new();
        private readonly IVoiceAudioCache _audioCache;
        private readonly INodeConfigurationService? _node;
        private ISpeechToText? _stt;
        private ITextToSpeech? _tts;
        private string _host = "";
        private int _port;
        private string _protocol = VoiceSatelliteOptions.Wyoming;
        private byte[]? _key;
        private string? _baseUrl;
        private string _language = "en-US";
        private Session? _session;
        private CancellationTokenSource? _stop;
        private Task? _loop;
        private string _state = "";
        private bool _wakeWarned;

        public VoiceSatellite(ILogger logger, IVoiceAudioCache audioCache, INodeConfigurationService nodeConfiguration) : base(logger)
        {
            _audioCache = audioCache;
            _node = nodeConfiguration;
        }

        // The tests' constructor: no node, so nodeBaseUrl must be set for esphome.
        internal VoiceSatellite(ILogger logger, IVoiceAudioCache? audioCache = null) : base(logger)
        {
            _audioCache = audioCache ?? new VoiceAudioCache();
        }

        /// <summary>The satellite's state now: <see cref="Idle"/>, <see cref="Listening"/>, … or <see cref="Offline"/>.</summary>
        public string SatelliteState { get { lock (_gate) return _state; } }

        // The connections and the HTTP calls; the tests replace them.
        internal virtual async Task<WyomingConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var client = await OpenTcpAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new WyomingConnection(client.GetStream(), client);
        }

        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Core.Utils.Web.SendAsync(request, (int)EngineTimeout.TotalMilliseconds, cancellationToken);

        private static async Task<TcpClient> OpenTcpAsync(string host, int port, CancellationToken cancellationToken)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                return client;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new TimeoutException($"No answer from {host}:{port} in {ConnectTimeout.TotalSeconds} s.");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public override void ConfigureDevice()
        {
            _host = (Options.Host ?? "").Trim();
            _protocol = (Options.Protocol ?? "").Trim();
            _port = Options.Port ?? (_protocol == VoiceSatelliteOptions.EspHome ? VoiceSatelliteOptions.EspHomePort : VoiceSatelliteOptions.WyomingPort);
            _key = null;
            var baseUrl = string.IsNullOrWhiteSpace(Options.NodeBaseUrl) ? NodeUrl() : Options.NodeBaseUrl;
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
            _language = (Options.Language ?? "").Trim();
            _stt = null;
            _tts = null;
            _wakeWarned = false;
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            var problem = Problem();
            if (problem != null)
                throw new InvalidOperationException(problem);
            _key = string.IsNullOrWhiteSpace(Options.EncryptionKey) ? null : Convert.FromBase64String(Options.EncryptionKey.Trim());
            _stt = CreateSpeechToText();
            _tts = CreateTextToSpeech();

            _stop = new CancellationTokenSource();
            var stop = _stop.Token;
            SetSatelliteState(Offline);
            _loop = Task.Run(() => RunAsync(stop), CancellationToken.None);
            return Task.CompletedTask;
        }

        protected override async Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            var stop = _stop;
            var loop = _loop;
            _stop = null;
            _loop = null;
            CancelAllTimers();
            if (stop == null)
                return;
            await stop.CancelAsync().ConfigureAwait(false);
            if (loop != null)
            {
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger.LogWarning("The voice satellite's connection didn't close in 10 seconds.");
                }
            }
            stop.Dispose();
        }

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;
            switch (command.Address)
            {
                case SpeakAddress:
                    await SpeakAsync(SpeechText(value), cancellationToken).ConfigureAwait(false);
                    break;
                case CancelAddress:
                    if (CommandBool(value))
                        await CancelRunAsync().ConfigureAwait(false);
                    break;
                case VolumeAddress:
                    await EspHome(command.Address).SetVolumeAsync(CommandPercent(value, VolumeAddress), cancellationToken).ConfigureAwait(false);
                    break;
                case MuteAddress:
                    await EspHome(command.Address).SetSwitchAsync(EspHomeSession.MuteSwitch, CommandBool(value), cancellationToken).ConfigureAwait(false);
                    break;
                case DoNotDisturbAddress:
                    await EspHome(command.Address).SetSwitchAsync(EspHomeSession.DoNotDisturbSwitch, CommandBool(value), cancellationToken).ConfigureAwait(false);
                    break;
                case LightAddress:
                    await EspHome(command.Address).SetLightAsync(LightChange.Parse(CommandJson(value ?? "")), cancellationToken).ConfigureAwait(false);
                    break;
                case WakeWordAddress:
                    await EspHome(command.Address).SetWakeWordAsync(CommandText(value ?? "").Trim(), cancellationToken).ConfigureAwait(false);
                    break;
                case StopRingingAddress:
                    if (CommandBool(value))
                        await EspHome(command.Address).PressButtonAsync(EspHomeSession.StopRingingButton, cancellationToken).ConfigureAwait(false);
                    break;
                case TimerAddress:
                    RequireEspHome(command.Address);
                    await TimerCommandAsync(CommandJson(value ?? ""), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException($"The command address '{command.Address}' isn't one of the voice satellite's.");
            }
        }

        private void RequireEspHome(string address)
        {
            if (_protocol != VoiceSatelliteOptions.EspHome)
                throw new InvalidOperationException($"The {address} command needs the satellite's protocol to be {VoiceSatelliteOptions.EspHome}.");
        }

        // The connected ESPHome session, for a command that needs one.
        private EspHomeSession EspHome(string address)
        {
            RequireEspHome(address);
            lock (_gate)
                return _session as EspHomeSession ?? throw new InvalidOperationException($"The voice satellite at {_host}:{_port} isn't connected.");
        }

        private async Task RunAsync(CancellationToken stop)
        {
            var failures = 0;
            var provisioned = false;
            while (!stop.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                var answered = false;
                var again = false;
                try
                {
                    var session = _protocol == VoiceSatelliteOptions.EspHome
                        ? await OpenEspHomeAsync(stop).ConfigureAwait(false)
                        : new WyomingSession(this, await ConnectAsync(_host, _port, stop).ConfigureAwait(false), stop);
                    if (session == null)
                    {
                        // The satellite was given its key: connect with it at once, but only once.
                        if (provisioned)
                            throw new InvalidOperationException("The satellite took the encryption key, but doesn't let it in.");
                        provisioned = again = true;
                        continue;
                    }
                    provisioned = false;
                    await SessionAsync(session, () => answered = true).ConfigureAwait(false);
                    if (!answered)
                        throw new IOException("The satellite closed the connection without answering.");
                    Logger.LogInformation("The voice satellite at {Host}:{Port} closed the connection.", _host, _port);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    if (failures == 0)
                        Logger.LogWarning("The voice satellite at {Host}:{Port} isn't connected: {Reason}", _host, _port, error.Message);
                    else
                        Logger.LogDebug("The voice satellite at {Host}:{Port} isn't connected: {Reason}", _host, _port, error.Message);
                    if (State == Core.DeviceState.Running)
                        SetState(Core.DeviceState.Running, $"Not connected to the satellite at {_host}:{_port}: {error.Message}");
                }
                finally
                {
                    if (!again)
                        SetSatelliteState(Offline);
                }
                if (again)
                    continue;

                // Only a connection that lasted starts the waits over: a satellite that answers and drops at once
                // is tried less and less often.
                if (answered && DateTime.UtcNow - started >= StableAfter)
                    failures = 0;
                var delay = ReconnectDelays[Math.Min(failures++, ReconnectDelays.Length - 1)];
                try { await Task.Delay(delay, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Runs one connection until it closes, goes silent or the device stops.
        private async Task SessionAsync(Session session, Action answered)
        {
            using var _ = session;
            session.OnAnswered = answered;
            var pinger = Task.CompletedTask;
            try
            {
                await session.StartAsync().ConfigureAwait(false);
                pinger = Task.Run(() => PingAsync(session), CancellationToken.None);
                using var alive = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                while (true)
                {
                    alive.CancelAfter(DeadAfter);
                    bool open;
                    try
                    {
                        open = await session.ReceiveAsync(alive.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!session.Token.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No answer from the satellite in {DeadAfter.TotalSeconds} s.");
                    }
                    if (!open)
                        return;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_session == session)
                        _session = null;
                    session.Run?.End();
                }
                await session.CancelAsync().ConfigureAwait(false);
                // Closing the connection aborts a write the pinger may still be in.
                await session.CloseAsync().ConfigureAwait(false);
                await pinger.ConfigureAwait(false);
            }
        }

        // The satellite answered: its connection is the one commands use from now on.
        private void Connected(Session session, string how)
        {
            lock (_gate)
                _session = session;
            SetSatelliteState(Idle);
            if (State == Core.DeviceState.Running)
                SetState(Core.DeviceState.Running);
            Logger.LogInformation("Connected to the voice satellite at {Host}:{Port} ({How}).", _host, _port, how);
        }

        private async Task PingAsync(Session session)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PingInterval, session.Token).ConfigureAwait(false);
                    await session.PingAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!session.Token.IsCancellationRequested)
                    Logger.LogDebug("Could not ping the voice satellite: {Reason}", error.Message);
                await session.CancelAsync().ConfigureAwait(false);
            }
        }

        // A request starts: the satellite heard its wake word, or its action button was pressed. A new request
        // replaces one still open.
        private void BeginRun(Session session)
        {
            lock (_gate)
            {
                session.Run?.End();
                session.Run = new Run(new EndpointSettings
                {
                    SilenceMs = Options.SilenceMs,
                    MaxRequestMs = Options.MaxRequestSeconds * 1000,
                    SpeechThresholdDb = Options.SpeechThresholdDb
                }, session.Token);
            }
            SetSatelliteState(Listening);
        }

        private void WarnWakeWordOnServer(object? stage)
        {
            if (!_wakeWarned)
                Logger.LogWarning("The voice satellite asks for the wake word to be heard on the server ({Stage}); " +
                    "pick a wake word engine on the satellite itself (on hassmic's settings page).", stage);
            _wakeWarned = true;
        }

        // The request's 16-bit mono audio, until the end of speech.
        private async Task AudioAsync(Session session, int rate, byte[] pcm)
        {
            Run? run;
            EndpointResult result;
            lock (_gate)
            {
                run = session.Run;
                if (run == null || run.Phase != RunPhase.Listening)
                    return;
                result = run.Add(rate, pcm);
                if (result != EndpointResult.Listening)
                    run.Phase = result == EndpointResult.Ended ? RunPhase.Recognizing : RunPhase.Done;
            }
            if (result == EndpointResult.Ended)
                _ = Task.Run(() => RecognizeAsync(session, run), CancellationToken.None);
            else if (result == EndpointResult.NoSpeech)
                await EndQuietlyAsync(session, run, "Nothing was said.").ConfigureAwait(false);
        }

        // The satellite heard the request end itself (the ESPHome API): what was said is recognized now.
        private async Task AudioEndedAsync(Session session)
        {
            Run? run;
            bool speech;
            lock (_gate)
            {
                run = session.Run;
                if (run == null || run.Phase != RunPhase.Listening)
                    return;
                speech = run.HeardSpeech;
                run.Phase = speech ? RunPhase.Recognizing : RunPhase.Done;
            }
            if (speech)
                _ = Task.Run(() => RecognizeAsync(session, run), CancellationToken.None);
            else
                await EndQuietlyAsync(session, run, "Nothing was said.").ConfigureAwait(false);
        }

        // The satellite gave the request up itself (the ESPHome API): nothing more is said to it about it.
        private void AbortRun(Session session)
        {
            lock (_gate)
            {
                var run = session.Run;
                if (run == null)
                    return;
                session.Run = null;
                run.End();
            }
            if (SatelliteState is Listening or Thinking)
                SetSatelliteState(Idle);
        }

        private async Task RecognizeAsync(Session session, Run run)
        {
            try
            {
                await session.SpeechEndedAsync().ConfigureAwait(false);
                SetSatelliteState(Thinking);

                string text;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Token))
                {
                    timeout.CancelAfter(EngineTimeout);
                    try
                    {
                        text = await _stt!.TranscribeAsync(run.Audio(), _language, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!run.Token.IsCancellationRequested)
                    {
                        throw new SpeechEngineException($"Speech to text took longer than {EngineTimeout.TotalSeconds} s.");
                    }
                }
                if (text.Length == 0)
                {
                    await EndQuietlyAsync(session, run, "No words were recognized.").ConfigureAwait(false);
                    return;
                }

                lock (_gate)
                {
                    if (run.Phase != RunPhase.Recognizing)
                        return;
                    run.Phase = RunPhase.AwaitingReply;
                }
                await session.TranscriptAsync(text).ConfigureAwait(false);
                Logger.LogDebug("The voice satellite heard: {Text}", text);
                Report(TranscriptAddress, JsonSerializer.SerializeToElement(text));

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Options.ReplyTimeoutSeconds), run.ReplyWait).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                lock (_gate)
                {
                    if (run.Phase != RunPhase.AwaitingReply)
                        return;
                }
                Logger.LogInformation("No speak command answered the request in {Seconds} s.", Options.ReplyTimeoutSeconds);
                await EndQuietlyAsync(session, run, "No reply came.").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
            catch (Exception error)
            {
                Logger.LogWarning("The voice satellite's request failed: {Reason}", error.Message);
                if (State == Core.DeviceState.Running && !session.Token.IsCancellationRequested)
                    SetState(Core.DeviceState.Running, error.Message);
                await EndAsync(session, run, "stt-failed", error.Message).ConfigureAwait(false);
            }
        }

        private async Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            Session session;
            Run? run;
            lock (_gate)
            {
                session = _session ?? throw new InvalidOperationException($"The voice satellite at {_host}:{_port} isn't connected.");
                // The answer to the open request; without one the speech is an announcement.
                run = session.Run is { Phase: RunPhase.AwaitingReply } open ? open : null;
                if (run != null)
                {
                    run.Phase = RunPhase.Replying;
                    run.ClaimReply();
                }
            }

            try
            {
                PcmAudio audio;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(EngineTimeout);
                    try
                    {
                        audio = await _tts!.SynthesizeAsync(text, _language, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new SpeechEngineException($"Text to speech took longer than {EngineTimeout.TotalSeconds} s.");
                    }
                }
                await PlayAsync(session, audio, text, run != null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // The request ends either way; a failure shows on the satellite and on the device.
                var cancelled = error is OperationCanceledException;
                if (!cancelled)
                {
                    Logger.LogWarning("The voice satellite's speech failed: {Reason}", error.Message);
                    if (State == Core.DeviceState.Running)
                        SetState(Core.DeviceState.Running, error.Message);
                }
                if (run != null)
                    await EndAsync(session, run, cancelled ? NoTextCode : "tts-failed", cancelled ? "The reply was cancelled." : error.Message).ConfigureAwait(false);
                throw;
            }

            lock (_gate)
            {
                if (run != null)
                    run.Phase = RunPhase.Done;
            }
            if (State == Core.DeviceState.Running && StateMessage.Length > 0 && SatelliteState != Offline)
                SetState(Core.DeviceState.Running);
        }

        private async Task PlayAsync(Session session, PcmAudio audio, string text, bool reply, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                // The connection may have dropped while the speech was made.
                if (_session != session)
                    throw new InvalidOperationException($"The voice satellite at {_host}:{_port} isn't connected.");
            }
            SetSatelliteState(Speaking);
            try
            {
                await session.PlayAsync(audio, text, reply, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                bool current;
                lock (_gate)
                    current = _session == session;
                // A dropped connection reports offline itself; only the current one goes back to idle here.
                if (current && SatelliteState == Speaking)
                    SetSatelliteState(Idle);
            }
        }

        // Waits until the satellite says the speech was played, at most its length and the grace: an older
        // satellite may not say so.
        private async Task WaitPlayedAsync(Session session, Task played, PcmAudio audio, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
            try
            {
                await played.WaitAsync(audio.Duration + PlayedGrace, linked.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.LogDebug("The voice satellite didn't say the speech was played.");
            }
        }

        private async Task CancelRunAsync()
        {
            Session? session;
            Run? run;
            lock (_gate)
            {
                session = _session;
                run = session?.Run;
                if (run is not { Phase: RunPhase.Listening or RunPhase.Recognizing or RunPhase.AwaitingReply })
                    return;
            }
            await EndQuietlyAsync(session!, run, "The request was cancelled.").ConfigureAwait(false);
        }

        private Task EndQuietlyAsync(Session session, Run run, string text) => EndAsync(session, run, NoTextCode, text);

        // Ends a request on the satellite: the no-text code quietly, any other code with its error animation.
        private async Task EndAsync(Session session, Run run, string code, string text)
        {
            lock (_gate)
            {
                if (session.Run != run)
                    return;
                session.Run = null;
                run.End();
            }
            try
            {
                await session.EndRunAsync(code, text).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
            {
                Logger.LogDebug("Could not end the voice satellite's request: {Reason}", error.Message);
            }
            if (SatelliteState is Listening or Thinking)
                SetSatelliteState(Idle);
        }

        private void SetSatelliteState(string state)
        {
            lock (_gate)
            {
                if (_state == state)
                    return;
                _state = state;
            }
            if (State == Core.DeviceState.Running)
                Report(StateAddress, JsonSerializer.SerializeToElement(state));
        }

        private void Report(string address, JsonElement value)
        {
            var template = ReportTemplates?.FirstOrDefault(x => x.Address == address);
            if (template == null || State != Core.DeviceState.Running)
                return;
            SendReport(this, new Report { Id = template.Id, TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Value = new ValueModel(value), Filter = "" });
        }

        // The URL the satellite downloads speech from: kept as a WAV file for an hour.
        private string AudioUrl(PcmAudio audio) => $"{_baseUrl}/api/voice/audio/{_audioCache.Add(Wav.Encode(audio))}";

        // The engines the options name; the tests replace them.
        internal virtual ISpeechToText CreateSpeechToText()
        {
            if (Options.SttEngine.Trim() == VoiceSatelliteOptions.OpenAiCompatible)
                return new OpenAiSpeechToText(HttpBase(Options.SttUrl), Default(Options.SttModel, "whisper-1"), Options.SttApiKey, SendAsync);
            var url = new Uri(Options.SttUrl.Trim());
            return new WyomingSpeechToText(url.Host, url.Port, ConnectAsync);
        }

        internal virtual ITextToSpeech CreateTextToSpeech()
        {
            var voice = (Options.TtsVoice ?? "").Trim();
            switch (Options.TtsEngine.Trim())
            {
                case VoiceSatelliteOptions.OpenAiCompatible:
                    return new OpenAiTextToSpeech(HttpBase(Options.TtsUrl), Default(Options.TtsModel, "tts-1"), Default(voice, "alloy"), Options.TtsApiKey, SendAsync);
                case VoiceSatelliteOptions.GoogleCloud:
                    return new GoogleCloudTextToSpeech(voice, Options.TtsApiKey, SendAsync);
                default:
                    var url = new Uri(Options.TtsUrl.Trim());
                    return new WyomingTextToSpeech(url.Host, url.Port, voice, ConnectAsync);
            }
        }

        private static string Default(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string HttpBase(string? url) => Default(url, VoiceSatelliteOptions.OpenAiUrl).TrimEnd('/');

        private string? NodeUrl()
        {
            try { return _node?.Configuration?.Url; }
            catch (Exception) { return null; }
        }

        private string? Problem()
        {
            if (!IsHost(_host))
                return "The host must be the satellite's IP address or host name.";
            if (_protocol is not (VoiceSatelliteOptions.Wyoming or VoiceSatelliteOptions.EspHome))
                return $"The protocol must be {VoiceSatelliteOptions.Wyoming} or {VoiceSatelliteOptions.EspHome}.";
            if (_port is < 1 or > 65535)
                return "The port must be 1-65535.";
            if (_protocol == VoiceSatelliteOptions.EspHome && EspHomeProblem() is string espHomeProblem)
                return espHomeProblem;
            if (_language.Length is 0 or > 35 || !_language.All(x => char.IsAsciiLetterOrDigit(x) || x == '-'))
                return "The language must be a language code, for example en-US or fi-FI.";

            var stt = (Options.SttEngine ?? "").Trim();
            if (stt is not (VoiceSatelliteOptions.Wyoming or VoiceSatelliteOptions.OpenAiCompatible))
                return $"The sttEngine must be {VoiceSatelliteOptions.Wyoming} or {VoiceSatelliteOptions.OpenAiCompatible}.";
            if (EngineUrlProblem("sttUrl", Options.SttUrl, stt) is string sttProblem)
                return sttProblem;
            if (!IsName(Options.SttModel))
                return "The sttModel must be a model name.";

            var tts = (Options.TtsEngine ?? "").Trim();
            if (tts is not (VoiceSatelliteOptions.Wyoming or VoiceSatelliteOptions.OpenAiCompatible or VoiceSatelliteOptions.GoogleCloud))
                return $"The ttsEngine must be {VoiceSatelliteOptions.Wyoming}, {VoiceSatelliteOptions.OpenAiCompatible} or {VoiceSatelliteOptions.GoogleCloud}.";
            if (tts != VoiceSatelliteOptions.GoogleCloud && EngineUrlProblem("ttsUrl", Options.TtsUrl, tts) is string ttsProblem)
                return ttsProblem;
            if (tts == VoiceSatelliteOptions.GoogleCloud && string.IsNullOrWhiteSpace(Options.TtsApiKey))
                return "googleCloud needs the ttsApiKey.";
            if (!IsName(Options.TtsModel))
                return "The ttsModel must be a model name.";
            if (!IsName(Options.TtsVoice))
                return "The ttsVoice must be a voice name, for example fi_FI-harri-medium.";

            if (Options.ReplyTimeoutSeconds is < 5 or > 300)
                return "The replyTimeoutSeconds must be 5-300.";
            if (Options.MaxRequestSeconds is < 3 or > 60)
                return "The maxRequestSeconds must be 3-60.";
            if (Options.SilenceMs is < 200 or > 3000)
                return "The silenceMs must be 200-3000.";
            if (double.IsNaN(Options.SpeechThresholdDb) || Options.SpeechThresholdDb is < -80 or > -10)
                return "The speechThresholdDb must be -80 to -10.";
            return null;
        }

        private string? EspHomeProblem()
        {
            var key = (Options.EncryptionKey ?? "").Trim();
            if (key.Length > 0)
            {
                Span<byte> bytes = stackalloc byte[64];
                if (!Convert.TryFromBase64String(key, bytes, out var length) || length != 32)
                    return "The encryptionKey must be 32 bytes in base64, for example from openssl rand -base64 32.";
                if (!bytes[..32].ContainsAnyExcept((byte)0))
                    return "The encryptionKey must not be all zeros.";
            }
            if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
                url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
                return "esphome needs the nodeBaseUrl: an http or https URL the satellite reaches this node at, for example http://192.168.1.10:8081.";
            if (url.IsLoopback)
                return "The nodeBaseUrl must be an address the satellite can reach, not localhost.";
            return null;
        }

        private static string? EngineUrlProblem(string key, string? value, string engine)
        {
            var text = (value ?? "").Trim();
            if (engine == VoiceSatelliteOptions.Wyoming)
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != "tcp" || url.Port is < 1 or > 65535 ||
                    !IsHost(url.Host) || url.UserInfo.Length > 0 || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0)
                    return $"wyoming needs the {key} as tcp://host:port, for example tcp://192.168.1.10:{(key == "sttUrl" ? 10300 : 10200)}.";
                return null;
            }
            if (text.Length == 0)
                return null;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var http) || http.Scheme is not ("http" or "https") ||
                http.UserInfo.Length > 0 || http.Query.Length > 0 || http.Fragment.Length > 0)
                return $"The {key} must be an http or https URL, for example {VoiceSatelliteOptions.OpenAiUrl}.";
            return null;
        }

        private static bool IsHost(string host) =>
            host.Length > 0 && !host.Any(x => char.IsWhiteSpace(x) || x is '/' or '\\' or '?' or '#' or '@') &&
            Uri.CheckHostName(host) != UriHostNameType.Unknown;

        private static bool IsName(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Trim().Length <= 100 && value.Trim().All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.' or ':' or '/');

        internal static string SpeechText(string value)
        {
            var text = CastSpeech.Normalize(CommandText(value));
            if (text.Length == 0)
                throw new ArgumentException("The text to speak is empty.", nameof(value));
            if (text.Length > MaxTextLength)
                throw new ArgumentException($"The text to speak is longer than {MaxTextLength} characters.", nameof(value));
            return text;
        }

        // A command value arrives as JSON: "Hello", true. Anything else is taken as text.
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
            var json = CommandJson(value ?? "");
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => json.GetRawText()
            };
        }

        private static bool CommandBool(string value)
        {
            var json = CommandJson(value ?? "");
            return json.ValueKind switch
            {
                JsonValueKind.False or JsonValueKind.Null => false,
                JsonValueKind.Number => json.GetDouble() != 0,
                JsonValueKind.String => !(json.GetString()?.Trim().ToLowerInvariant() is "false" or "0" or "off" or ""),
                _ => true
            };
        }

        private static int CommandPercent(string value, string address)
        {
            var json = CommandJson(value ?? "");
            double number;
            if (json.ValueKind == JsonValueKind.Number)
                number = json.GetDouble();
            else if (json.ValueKind != JsonValueKind.String || !double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new ArgumentException($"The {address} must be a number 0-100.", nameof(value));
            if (double.IsNaN(number) || number is < 0 or > 100)
                throw new ArgumentException($"The {address} must be 0-100.", nameof(value));
            return (int)Math.Round(number);
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            return new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Voice satellite",
                ClassFullName = GetType().FullName!,
                // The node adds the parameters from VoiceSatelliteOptions.
                ReportTemplates =
                [
                    ReportTemplate(StateAddress, "State", ValueType.Text, Idle, new ValueSchema
                    {
                        Type = "string",
                        Title = "Satellite state",
                        Enum = [Idle, Listening, Thinking, Speaking, Offline],
                        ReadOnly = true
                    }),
                    ReportTemplate(TranscriptAddress, "Transcript", ValueType.Text, "What is the temperature in the living room?",
                        new ValueSchema { Type = "string", Semantic = ValueSemantics.Text, Title = "What was said", ReadOnly = true }),
                    ReportTemplate(VolumeAddress, "Volume", ValueType.Number, 50,
                        Described(ValueSchema.Integer("%", minimum: 0, maximum: 100), "Volume (esphome)", readOnly: true)),
                    ReportTemplate(MuteAddress, "Microphone muted", ValueType.Boolean, false,
                        Described(ValueSchema.Boolean(), "Microphone muted (esphome)", readOnly: true)),
                    ReportTemplate(DoNotDisturbAddress, "Do not disturb", ValueType.Boolean, false,
                        Described(ValueSchema.Boolean(), "Do not disturb (esphome)", readOnly: true)),
                    ReportTemplate(LightAddress, "Light ring", ValueType.Entity, LightModel(), LightSchema(readOnly: true)),
                    ReportTemplate(WakeWordAddress, "Wake word", ValueType.Text, "alexa",
                        Described(ValueSchema.Text(), "Wake word in use (esphome)", readOnly: true)),
                    ReportTemplate(ButtonAddress, "Button", ValueType.Text, "action_press",
                        Described(ValueSchema.Text(), "Button event (esphome)", readOnly: true)),
                    ReportTemplate(TimerRingingAddress, "Timer ringing", ValueType.Boolean, false,
                        Described(ValueSchema.Boolean(), "A timer rings (esphome)", readOnly: true))
                ],
                CommandTemplates =
                [
                    CommandTemplate(SpeakAddress, "Speak", ValueType.Text, "It is 21 degrees in the living room.",
                        new ValueSchema { Type = "string", Semantic = ValueSemantics.Text, Title = "Text to speak", Description = $"The answer to the open request, or an announcement; at most {MaxTextLength} characters" }),
                    CommandTemplate(CancelAddress, "Cancel request", ValueType.Boolean, true, null),
                    CommandTemplate(VolumeAddress, "Set volume", ValueType.Number, 50,
                        Described(ValueSchema.Integer("%", minimum: 0, maximum: 100), "Volume (esphome)")),
                    CommandTemplate(MuteAddress, "Mute microphone", ValueType.Boolean, true,
                        Described(ValueSchema.Boolean(ValueSemantics.Switch), "Microphone muted (esphome)")),
                    CommandTemplate(DoNotDisturbAddress, "Do not disturb", ValueType.Boolean, true,
                        Described(ValueSchema.Boolean(ValueSemantics.Switch), "Do not disturb (esphome)")),
                    CommandTemplate(LightAddress, "Set light ring", ValueType.Entity, LightModel(), LightSchema(readOnly: false)),
                    CommandTemplate(WakeWordAddress, "Set wake word", ValueType.Text, "alexa",
                        Described(ValueSchema.Text(), "Wake word (esphome)", "A wake word the satellite lists, by its id or name")),
                    CommandTemplate(TimerAddress, "Timer", ValueType.Entity, new Dictionary<string, object> { ["seconds"] = 300, ["name"] = "Pasta" },
                        Described(ValueSchema.Entity(new Dictionary<string, ValueSchema>
                        {
                            ["seconds"] = ValueSchema.Integer("s", minimum: 1, maximum: MaxTimerSeconds),
                            ["name"] = ValueSchema.Text(),
                            ["cancel"] = ValueSchema.Boolean()
                        }), "Timer (esphome)", "{seconds, name} starts a timer the satellite rings; {cancel: true, name} cancels it, without a name all")),
                    CommandTemplate(StopRingingAddress, "Stop ringing", ValueType.Boolean, true, null)
                ]
            };
        }

        private static ValueSchema Described(ValueSchema schema, string title, string? description = null, bool readOnly = false)
        {
            schema.Title = title;
            schema.Description = description;
            if (readOnly)
                schema.ReadOnly = true;
            return schema;
        }

        private static ReportTemplate ReportTemplate(string address, string name, ValueType type, object model, ValueSchema schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = address,
            Name = name,
            Type = type,
            Model = model,
            Schema = schema
        };

        private static CommandTemplate CommandTemplate(string address, string name, ValueType type, object model, ValueSchema? schema) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Address = address,
            Name = name,
            Type = type,
            Model = model,
            Schema = schema
        };

        private static Dictionary<string, object> LightModel() =>
            new() { ["on"] = true, ["brightness"] = 80, ["color"] = "#ff8800", ["effect"] = "None" };

        private static ValueSchema LightSchema(bool readOnly) =>
            Described(ValueSchema.Entity(new Dictionary<string, ValueSchema>
            {
                ["on"] = ValueSchema.Boolean(ValueSemantics.Switch),
                ["brightness"] = ValueSchema.Integer("%", ValueSemantics.Brightness, 0, 100),
                ["color"] = ValueSchema.Text(ValueSemantics.Color),
                ["effect"] = ValueSchema.Text()
            }, ValueSemantics.Light), "Light ring (esphome)", "on, brightness 0-100, color #rrggbb, effect; a command sets what it names", readOnly);

        private enum RunPhase { Listening, Recognizing, AwaitingReply, Replying, Done }

        // One connection to the satellite, whichever protocol. Its token is kept, so it can still be read once the
        // session is disposed (cancelled first): a command that outlives its session fails as cancelled.
        private abstract class Session : IDisposable
        {
            private readonly CancellationTokenSource _cancel;

            protected Session(CancellationToken stop)
            {
                _cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
                Token = _cancel.Token;
            }

            public CancellationToken Token { get; }
            public Run? Run { get; set; }

            /// <summary>Called once, when the satellite first answers.</summary>
            public Action? OnAnswered { get; set; }

            /// <summary>What the client says first.</summary>
            public abstract Task StartAsync();

            /// <summary>Reads and handles one message; false when the satellite closed the connection.</summary>
            public abstract Task<bool> ReceiveAsync(CancellationToken cancellationToken);

            public abstract Task PingAsync();

            /// <summary>The node heard the request end: the satellite's microphone goes off.</summary>
            public abstract Task SpeechEndedAsync();

            /// <summary>What speech to text understood; the request awaits its answer.</summary>
            public abstract Task TranscriptAsync(string text);

            /// <summary>Ends the open request with an error code, quietly for <see cref="NoTextCode"/>.</summary>
            public abstract Task EndRunAsync(string code, string text);

            /// <summary>Plays the speech, the open request's answer or an announcement, and waits until it's played.</summary>
            public abstract Task PlayAsync(PcmAudio audio, string text, bool reply, CancellationToken cancellationToken);

            public abstract ValueTask CloseAsync();

            public async Task CancelAsync()
            {
                try { await _cancel.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }

            public virtual void Dispose() => _cancel.Dispose();
        }

        // One request: from the satellite's start to its reply.
        private sealed class Run(EndpointSettings settings, CancellationToken session)
        {
            private readonly CancellationTokenSource _cancel = CancellationTokenSource.CreateLinkedTokenSource(session);
            private readonly CancellationTokenSource _replyWait = new();
            private readonly MemoryStream _audio = new();
            private EndpointDetector? _detector;
            private int _rate;

            public RunPhase Phase { get; set; } = RunPhase.Listening;
            public CancellationToken Token => _cancel.Token;
            public CancellationToken ReplyWait => _replyWait.Token;
            public bool HeardSpeech => _detector?.HeardSpeech ?? false;

            public EndpointResult Add(int rate, byte[] pcm)
            {
                if (_detector == null)
                {
                    _rate = rate;
                    _detector = new EndpointDetector(settings with { SampleRate = rate });
                }
                _audio.Write(pcm);
                return _detector.Process(pcm);
            }

            public PcmAudio Audio() => new(_rate, 1, _audio.ToArray());

            // A speak command took the reply: the reply timeout no longer applies.
            public void ClaimReply() => _replyWait.Cancel();

            public void End()
            {
                Phase = RunPhase.Done;
                _replyWait.Cancel();
                _cancel.Cancel();
            }
        }
    }
}
