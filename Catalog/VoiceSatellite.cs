using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Voice;
using RIoT2.Net.Devices.Services.Wyoming;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="VoiceSatellite"/> device's parameters.</summary>
    public sealed class VoiceSatelliteOptions
    {
        public const string Wyoming = "wyoming";
        public const string OpenAiCompatible = "openAiCompatible";
        public const string GoogleCloud = "googleCloud";
        public const string OpenAiUrl = "https://api.openai.com/v1";

        [DeviceParameter("host", Required = true, Description = "IP address or host name of the voice satellite, for example the Echo running hassmic in Wyoming mode")]
        public string Host { get; set; } = "";

        [DeviceParameter("port", Default = "16700", Description = "The satellite's Wyoming port: 16700 for hassmic")]
        public int Port { get; set; } = 16700;

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
    /// A voice satellite on the Wyoming protocol, such as an Amazon Echo running echo-dot-assist's hassmic with
    /// <c>PROTO=wyoming</c>. The satellite hears its wake word; the node finds the end of the request, turns it into
    /// text and reports it as <c>transcript</c>. A workflow answers with the <c>speak</c> command, whose speech the
    /// satellite plays. The node keeps one connection to the satellite and connects again when it drops.
    /// </summary>
    [RequiresHardware("Wyoming voice satellite on the network")]
    public class VoiceSatellite : AsyncDeviceBase<VoiceSatelliteOptions>, IAsyncCommandDevice, IDeviceWithConfiguration
    {
        public const string StateAddress = "state";
        public const string TranscriptAddress = "transcript";
        public const string SpeakAddress = "speak";
        public const string CancelAddress = "cancel";

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

        /// <summary>A connection without any event for this long is dead.</summary>
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
        private ISpeechToText? _stt;
        private ITextToSpeech? _tts;
        private string _host = "";
        private string _language = "en-US";
        private Session? _session;
        private CancellationTokenSource? _stop;
        private Task? _loop;
        private string _state = "";
        private bool _wakeWarned;

        public VoiceSatellite(ILogger logger) : base(logger) { }

        /// <summary>The satellite's state now: <see cref="Idle"/>, <see cref="Listening"/>, … or <see cref="Offline"/>.</summary>
        public string SatelliteState { get { lock (_gate) return _state; } }

        // The connections and the HTTP calls; the tests replace them.
        internal virtual async Task<WyomingConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                return new WyomingConnection(client.GetStream(), client);
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

        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Core.Utils.Web.SendAsync(request, (int)EngineTimeout.TotalMilliseconds, cancellationToken);

        public override void ConfigureDevice()
        {
            _host = (Options.Host ?? "").Trim();
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
                default:
                    throw new ArgumentException($"The command address '{command.Address}' is not one of {SpeakAddress} or {CancelAddress}.");
            }
        }

        private async Task RunAsync(CancellationToken stop)
        {
            var failures = 0;
            while (!stop.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                var answered = false;
                try
                {
                    var connection = await ConnectAsync(_host, Options.Port, stop).ConfigureAwait(false);
                    await SessionAsync(connection, stop, () => answered = true).ConfigureAwait(false);
                    if (!answered)
                        throw new IOException("The satellite closed the connection without answering.");
                    Logger.LogInformation("The voice satellite at {Host}:{Port} closed the connection.", _host, Options.Port);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    if (failures == 0)
                        Logger.LogWarning("The voice satellite at {Host}:{Port} isn't connected: {Reason}", _host, Options.Port, error.Message);
                    else
                        Logger.LogDebug("The voice satellite at {Host}:{Port} isn't connected: {Reason}", _host, Options.Port, error.Message);
                    if (State == Core.DeviceState.Running)
                        SetState(Core.DeviceState.Running, $"Not connected to the satellite at {_host}:{Options.Port}: {error.Message}");
                }
                SetSatelliteState(Offline);

                // Only a connection that lasted starts the waits over: a satellite that answers and drops at once
                // is tried less and less often.
                if (answered && DateTime.UtcNow - started >= StableAfter)
                    failures = 0;
                var delay = ReconnectDelays[Math.Min(failures++, ReconnectDelays.Length - 1)];
                try { await Task.Delay(delay, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task SessionAsync(WyomingConnection connection, CancellationToken stop, Action answered)
        {
            await using var _ = connection.ConfigureAwait(false);
            using var session = new Session(connection, stop);
            await connection.WriteAsync("describe", null, session.Token).ConfigureAwait(false);
            await connection.WriteAsync("run-satellite", null, session.Token).ConfigureAwait(false);

            var pinger = Task.Run(() => PingAsync(session), CancellationToken.None);
            using var alive = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            try
            {
                while (true)
                {
                    alive.CancelAfter(DeadAfter);
                    WyomingEvent? message;
                    try
                    {
                        message = await connection.ReadAsync(alive.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!session.Token.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No answer from the satellite in {DeadAfter.TotalSeconds} s.");
                    }
                    if (message == null)
                        return;
                    if (!session.Answered)
                    {
                        // The satellite's first event (its answer to describe) shows the connection works.
                        session.Answered = true;
                        answered();
                        lock (_gate)
                            _session = session;
                        SetSatelliteState(Idle);
                        if (State == Core.DeviceState.Running)
                            SetState(Core.DeviceState.Running);
                        Logger.LogInformation("Connected to the voice satellite at {Host}:{Port}.", _host, Options.Port);
                    }
                    await HandleAsync(session, message).ConfigureAwait(false);
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
                await connection.DisposeAsync().ConfigureAwait(false);
                await pinger.ConfigureAwait(false);
            }
        }

        private async Task PingAsync(Session session)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PingInterval, session.Token).ConfigureAwait(false);
                    await session.Connection.WriteAsync("ping", null, session.Token).ConfigureAwait(false);
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

        private async Task HandleAsync(Session session, WyomingEvent message)
        {
            switch (message.Type)
            {
                case "run-pipeline":
                    StartRun(session, message);
                    break;
                case "audio-chunk":
                    await AudioAsync(session, message).ConfigureAwait(false);
                    break;
                case "played":
                    session.Played?.TrySetResult();
                    break;
                case "ping":
                    await session.Connection.WriteAsync("pong", null, session.Token).ConfigureAwait(false);
                    break;
                case "info":
                    if (message.Data["satellite"] is JsonObject satellite && satellite["name"] is JsonValue name)
                        Logger.LogInformation("The voice satellite at {Host} is {Name}.", _host, name.ToString());
                    break;
            }
        }

        private void StartRun(Session session, WyomingEvent message)
        {
            var stage = message.GetString("start_stage") ?? "";
            lock (_gate)
            {
                // A new request (the wake word again, or the button) replaces one still open.
                session.Run?.End();
                session.Run = null;
                if (stage != "asr")
                {
                    if (!_wakeWarned)
                        Logger.LogWarning("The voice satellite asks for the wake word to be heard on the server (start_stage {Stage}); " +
                            "pick a wake word engine on the satellite itself (on hassmic's settings page).", stage);
                    _wakeWarned = true;
                    return;
                }
                session.Run = new Run(new EndpointSettings
                {
                    SilenceMs = Options.SilenceMs,
                    MaxRequestMs = Options.MaxRequestSeconds * 1000,
                    SpeechThresholdDb = Options.SpeechThresholdDb
                }, session.Token);
            }
            SetSatelliteState(Listening);
        }

        private async Task AudioAsync(Session session, WyomingEvent message)
        {
            Run? run;
            EndpointResult result;
            lock (_gate)
            {
                run = session.Run;
                if (run == null || run.Phase != RunPhase.Listening)
                    return;
                if (message.GetInt("width") is int width && width != 2 || message.GetInt("channels") is int channels && channels != 1)
                {
                    Logger.LogWarning("The voice satellite sends audio that isn't 16-bit mono; the request is dropped.");
                    run.Phase = RunPhase.Done;
                    result = EndpointResult.NoSpeech;
                }
                else
                {
                    result = run.Add(message.GetInt("rate") ?? 16000, message.Payload);
                    if (result != EndpointResult.Listening)
                        run.Phase = result == EndpointResult.Ended ? RunPhase.Recognizing : RunPhase.Done;
                }
            }
            if (result == EndpointResult.Ended)
                _ = Task.Run(() => RecognizeAsync(session, run), CancellationToken.None);
            else if (result == EndpointResult.NoSpeech)
                await EndQuietlyAsync(session, run, "Nothing was said.").ConfigureAwait(false);
        }

        private async Task RecognizeAsync(Session session, Run run)
        {
            try
            {
                await session.Connection.WriteAsync("voice-stopped", null, session.Token).ConfigureAwait(false);
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
                await session.Connection.WriteAsync("transcript", new JsonObject { ["text"] = text }, session.Token).ConfigureAwait(false);
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
                session = _session ?? throw new InvalidOperationException($"The voice satellite at {_host}:{Options.Port} isn't connected.");
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
                await PlayAsync(session, audio, cancellationToken).ConfigureAwait(false);
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

        private async Task PlayAsync(Session session, PcmAudio audio, CancellationToken cancellationToken)
        {
            var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                // The connection may have dropped while the speech was made.
                if (_session != session)
                    throw new InvalidOperationException($"The voice satellite at {_host}:{Options.Port} isn't connected.");
                session.Played = played;
            }
            SetSatelliteState(Speaking);
            try
            {
                // Written on the connection's token: a cancelled command doesn't cut the speech off mid-event.
                var format = WyomingEvent.AudioFormat(audio.Rate, audio.Channels);
                await session.Connection.WriteAsync("audio-start", format, session.Token).ConfigureAwait(false);
                foreach (var chunk in SpeechEngines.Chunks(audio))
                    await session.Connection.WriteAsync(new WyomingEvent("audio-chunk", WyomingEvent.AudioFormat(audio.Rate, audio.Channels), chunk), session.Token).ConfigureAwait(false);
                await session.Connection.WriteAsync("audio-stop", null, session.Token).ConfigureAwait(false);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);
                try
                {
                    await played.Task.WaitAsync(audio.Duration + PlayedGrace, linked.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger.LogDebug("The voice satellite didn't say the speech was played.");
                }
            }
            finally
            {
                bool current;
                lock (_gate)
                {
                    if (session.Played == played)
                        session.Played = null;
                    current = _session == session;
                }
                // A dropped connection reports offline itself; only the current one goes back to idle here.
                if (current && SatelliteState == Speaking)
                    SetSatelliteState(Idle);
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
                await session.Connection.WriteAsync("error", new JsonObject { ["code"] = code, ["text"] = text }, session.Token).ConfigureAwait(false);
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

        private string? Problem()
        {
            if (!IsHost(_host))
                return "The host must be the satellite's IP address or host name.";
            if (Options.Port is < 1 or > 65535)
                return "The port must be 1-65535.";
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
                    new ReportTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = StateAddress,
                        Name = "State",
                        Type = ValueType.Text,
                        Model = Idle,
                        Schema = new ValueSchema
                        {
                            Type = "string",
                            Title = "Satellite state",
                            Enum = [Idle, Listening, Thinking, Speaking, Offline],
                            ReadOnly = true
                        }
                    },
                    new ReportTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = TranscriptAddress,
                        Name = "Transcript",
                        Type = ValueType.Text,
                        Model = "What is the temperature in the living room?",
                        Schema = new ValueSchema { Type = "string", Semantic = ValueSemantics.Text, Title = "What was said", ReadOnly = true }
                    }
                ],
                CommandTemplates =
                [
                    new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = SpeakAddress,
                        Name = "Speak",
                        Type = ValueType.Text,
                        Model = "It is 21 degrees in the living room.",
                        Schema = new ValueSchema { Type = "string", Semantic = ValueSemantics.Text, Title = "Text to speak", Description = $"The answer to the open request, or an announcement; at most {MaxTextLength} characters" }
                    },
                    new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = CancelAddress,
                        Name = "Cancel request",
                        Type = ValueType.Boolean,
                        Model = true
                    }
                ]
            };
        }

        private enum RunPhase { Listening, Recognizing, AwaitingReply, Replying, Done }

        // One connection to the satellite. Its token is kept, so it can still be read once the session is disposed
        // (cancelled first): a command that outlives its session fails as cancelled.
        private sealed class Session : IDisposable
        {
            private readonly CancellationTokenSource _cancel;

            public Session(WyomingConnection connection, CancellationToken stop)
            {
                Connection = connection;
                _cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
                Token = _cancel.Token;
            }

            public WyomingConnection Connection { get; }
            public CancellationToken Token { get; }
            public bool Answered { get; set; }
            public Run? Run { get; set; }
            public TaskCompletionSource? Played { get; set; }

            public async Task CancelAsync()
            {
                try { await _cancel.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }

            public void Dispose() => _cancel.Dispose();
        }

        // One request: from the satellite's run-pipeline to its reply.
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
