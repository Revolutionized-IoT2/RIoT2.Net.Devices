using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Interfaces;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="GoogleCast"/> device's parameters.</summary>
    public sealed class GoogleCastOptions
    {
        public const string GoogleTranslate = "googleTranslate";
        public const string GoogleCloud = "googleCloud";

        [DeviceParameter("ipAddress", Required = true, Description = "IP address of the speaker or speaker group, for example 192.168.1.20")]
        public string IpAddress { get; set; } = "";

        [DeviceParameter("port", Default = "8009", Description = "Cast port: 8009 for a speaker; a speaker group has its own port (see the guide)")]
        public int Port { get; set; } = 8009;

        [DeviceParameter("ttsEngine", Default = GoogleTranslate, Description = "googleTranslate (free, no key) or googleCloud (Google Cloud Text-to-Speech, needs cloudTtsApiKey)")]
        public string TtsEngine { get; set; } = GoogleTranslate;

        [DeviceParameter("language", Default = "en-GB", Description = "Language of the speech, for example en-GB, en-US or fi-FI")]
        public string Language { get; set; } = "en-GB";

        [DeviceParameter("voice", Description = "googleCloud: the voice, for example en-GB-Neural2-B; empty: Google's default voice for the language")]
        public string Voice { get; set; } = "";

        [DeviceParameter("cloudTtsApiKey", Secret = true, Description = "googleCloud: API key of a Google Cloud project with the Text-to-Speech API")]
        public string CloudTtsApiKey { get; set; } = "";

        [DeviceParameter("nodeBaseUrl", Description = "googleCloud: URL the speaker reaches this node at, for example http://192.168.1.10:8081; empty: the node's RIOT2_NODE_URL")]
        public string NodeBaseUrl { get; set; } = "";

        [DeviceParameter("announcementVolume", Description = "Volume 0-100 for speak and playUrl; empty: keep the speaker's volume")]
        public int? AnnouncementVolume { get; set; }

        [DeviceParameter("restoreVolume", Default = "true", Description = "Set the speaker's volume back after speak and playUrl")]
        public bool RestoreVolume { get; set; } = true;
    }

    /// <summary>
    /// A Google Cast speaker (Google Home, Nest Audio, Nest Mini, a Chromecast, a speaker group): <c>speak</c> says a
    /// text, <c>playUrl</c> plays a media URL, <c>setVolume</c> sets the volume and <c>stop</c> stops what it plays.
    /// The node connects to the speaker's address for each command; an announcement stops what the speaker played.
    /// The node runs one command of a device at a time, so announcements queue up.
    /// </summary>
    [RequiresHardware("Google Cast speaker on the network")]
    public class GoogleCast : AsyncDeviceBase<GoogleCastOptions>, IAsyncCommandDevice, IDeviceWithConfiguration
    {
        public const string SpeakAddress = "speak";
        public const string PlayUrlAddress = "playUrl";
        public const string SetVolumeAddress = "setVolume";
        public const string StopAddress = "stop";

        /// <summary>The longest text <c>speak</c> takes.</summary>
        public const int MaxTextLength = 1000;

        /// <summary>The longest URL <c>playUrl</c> takes.</summary>
        public const int MaxUrlLength = 2048;

        /// <summary>
        /// How long <c>speak</c> and <c>playUrl</c> wait for the media to end before they set the volume back and
        /// return; longer media keeps playing.
        /// </summary>
        internal static readonly TimeSpan MaxPlayTime = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(10);

        private readonly ICastAudioCache _audioCache;
        private readonly INodeConfigurationService? _node;
        private string _host = "";
        private string _engine = GoogleCastOptions.GoogleTranslate;
        private string _language = "en-GB";
        private string _voice = "";
        private string? _baseUrl;

        public GoogleCast(ILogger logger, ICastAudioCache audioCache, INodeConfigurationService nodeConfiguration) : base(logger)
        {
            _audioCache = audioCache;
            _node = nodeConfiguration;
        }

        // The tests' constructor: no node, so nodeBaseUrl must be set for googleCloud.
        internal GoogleCast(ILogger logger, ICastAudioCache audioCache) : base(logger)
        {
            _audioCache = audioCache;
        }

        public override void ConfigureDevice()
        {
            _host = (Options.IpAddress ?? "").Trim();
            _engine = (Options.TtsEngine ?? "").Trim();
            _language = (Options.Language ?? "").Trim();
            _voice = (Options.Voice ?? "").Trim();
            var baseUrl = string.IsNullOrWhiteSpace(Options.NodeBaseUrl) ? NodeUrl() : Options.NodeBaseUrl;
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            var problem = Problem();
            if (problem != null)
                throw new InvalidOperationException(problem);
            return Task.CompletedTask;
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) => Task.CompletedTask;

        // The speaker connection and the call to Google Cloud; the tests replace them.
        internal virtual ICastSession CreateSession() => new SharpCasterSession();

        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Core.Utils.Web.SendAsync(request, cancellationToken: cancellationToken);

        public async Task ExecuteCommandAsync(string commandId, string value, CancellationToken cancellationToken)
        {
            var command = CommandTemplates?.FirstOrDefault(x => x.Id == commandId);
            if (command == null)
                return;

            try
            {
                switch (command.Address)
                {
                    case SpeakAddress:
                        var text = SpeechText(value);
                        await PlayAsync(await SpeechAsync(text, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                        break;
                    case PlayUrlAddress:
                        await PlayAsync([MediaOf(MediaUrl(value))], cancellationToken).ConfigureAwait(false);
                        break;
                    case SetVolumeAddress:
                        var volume = Volume(value);
                        await WithSessionAsync((session, token) => session.SetVolumeAsync(volume / 100.0, token), cancellationToken).ConfigureAwait(false);
                        break;
                    case StopAddress:
                        if (!CommandBool(value))
                            return;
                        await WithSessionAsync((session, token) => session.StopAsync(token), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentException($"The command address '{command.Address}' is not one of {SpeakAddress}, {PlayUrlAddress}, {SetVolumeAddress} or {StopAddress}.");
                }
                if (State == Core.DeviceState.Running && StateMessage.Length > 0)
                    SetState(Core.DeviceState.Running);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is CastException or CastSpeechException or HttpRequestException or TaskCanceledException)
            {
                // The speaker or Google failing isn't the configuration's fault: the device keeps running, shows why,
                // and the command fails.
                Logger.LogWarning("Google Cast {Command} failed: {Reason}", command.Address, error.Message);
                if (State == Core.DeviceState.Running)
                    SetState(Core.DeviceState.Running, error.Message);
                throw;
            }
        }

        private async Task<IReadOnlyList<CastMedia>> SpeechAsync(string text, CancellationToken cancellationToken)
        {
            if (_engine == GoogleCastOptions.GoogleTranslate)
                return CastSpeech.TranslateUrls(text, _language).Select(url => new CastMedia(url, "audio/mpeg", text)).ToList();

            var key = CastSpeech.Key(_engine, _language, _voice, text);
            var token = _audioCache.Find(key);
            if (token == null)
            {
                using var request = CastSpeech.CloudRequest(text, _language, _voice, Options.CloudTtsApiKey);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                token = _audioCache.Add(key, await CastSpeech.CloudAudioAsync(response, cancellationToken).ConfigureAwait(false));
            }
            return [new CastMedia($"{_baseUrl}/api/cast/audio/{token}", "audio/mpeg", text)];
        }

        private async Task PlayAsync(IReadOnlyList<CastMedia> media, CancellationToken cancellationToken)
        {
            await using var session = CreateSession();
            await session.ConnectAsync(_host, Options.Port, cancellationToken).ConfigureAwait(false);

            double? previous = null;
            if (Options.AnnouncementVolume != null && Options.RestoreVolume)
                previous = await session.GetVolumeAsync(cancellationToken).ConfigureAwait(false);

            var ended = true;
            try
            {
                // Inside the try: a change that timed out may still have reached the speaker, so it is set back too.
                if (Options.AnnouncementVolume is int announcement)
                    await session.SetVolumeAsync(announcement / 100.0, cancellationToken).ConfigureAwait(false);

                foreach (var item in media)
                {
                    if (!await session.PlayAsync(item, MaxPlayTime, cancellationToken).ConfigureAwait(false))
                    {
                        Logger.LogInformation("Google Cast media still plays after {Seconds} s; not waiting longer", MaxPlayTime.TotalSeconds);
                        ended = false;
                        break;
                    }
                }
                // Back to idle, so the speaker doesn't stay in the media player.
                if (ended)
                    await session.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (previous is double level && Math.Abs(level - Options.AnnouncementVolume!.Value / 100.0) > 0.001)
                    await RestoreVolumeAsync(session, level).ConfigureAwait(false);
            }
        }

        // Also after a failure or a cancellation, so the speaker isn't left loud; a failure here is only logged.
        private async Task RestoreVolumeAsync(ICastSession session, double level)
        {
            using var timeout = new CancellationTokenSource(RestoreTimeout);
            try
            {
                await session.SetVolumeAsync(level, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Logger.LogWarning("Could not set the Google Cast speaker's volume back: {Reason}", error.Message);
            }
        }

        private async Task WithSessionAsync(Func<ICastSession, CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            await using var session = CreateSession();
            await session.ConnectAsync(_host, Options.Port, cancellationToken).ConfigureAwait(false);
            await action(session, cancellationToken).ConfigureAwait(false);
        }

        private string? Problem()
        {
            if (_host.Length == 0 || _host.Any(x => char.IsWhiteSpace(x) || x is '/' or '\\' or '?' or '#' or '@') ||
                Uri.CheckHostName(_host) == UriHostNameType.Unknown)
                return "The ipAddress must be the speaker's IP address or host name.";
            if (Options.Port is < 1 or > 65535)
                return "The port must be 1-65535.";
            if (_engine is not (GoogleCastOptions.GoogleTranslate or GoogleCastOptions.GoogleCloud))
                return $"The ttsEngine must be {GoogleCastOptions.GoogleTranslate} or {GoogleCastOptions.GoogleCloud}.";
            if (_language.Length is 0 or > 35 || !_language.All(x => char.IsAsciiLetterOrDigit(x) || x == '-'))
                return "The language must be a language code, for example en-GB.";
            if (Options.AnnouncementVolume is < 0 or > 100)
                return "The announcementVolume must be 0-100, or empty.";
            if (_engine == GoogleCastOptions.GoogleCloud)
            {
                if (string.IsNullOrWhiteSpace(Options.CloudTtsApiKey))
                    return "googleCloud needs the cloudTtsApiKey.";
                if (_voice.Length > 0 && !_voice.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_'))
                    return "The voice must be a voice name, for example en-GB-Neural2-B.";
                if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
                    !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
                    return "googleCloud needs the nodeBaseUrl: an http or https URL the speaker reaches this node at, for example http://192.168.1.10:8081.";
                if (url.IsLoopback)
                    return "The nodeBaseUrl must be an address the speaker can reach, not localhost.";
            }
            return null;
        }

        private string? NodeUrl()
        {
            try { return _node?.Configuration?.Url; }
            catch (Exception) { return null; }
        }

        internal static string SpeechText(string value)
        {
            var text = CastSpeech.Normalize(CommandText(value));
            if (text.Length == 0)
                throw new ArgumentException("The text to speak is empty.", nameof(value));
            if (text.Length > MaxTextLength)
                throw new ArgumentException($"The text to speak is longer than {MaxTextLength} characters.", nameof(value));
            return text;
        }

        internal static string MediaUrl(string value)
        {
            var text = CommandText(value).Trim();
            if (text.Length > MaxUrlLength || !Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(url.UserInfo))
                throw new ArgumentException("The media must be an http or https URL.", nameof(value));
            return url.AbsoluteUri;
        }

        internal static int Volume(string value)
        {
            var json = CommandJson(value);
            double number;
            if (json.ValueKind == JsonValueKind.Number)
                number = json.GetDouble();
            else if (json.ValueKind != JsonValueKind.String || !double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                throw new ArgumentException($"The volume '{value}' is not a number.", nameof(value));
            if (double.IsNaN(number) || number is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(value), number, "The volume must be 0-100.");
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        }

        internal static CastMedia MediaOf(string url)
        {
            var path = new Uri(url).AbsolutePath;
            var type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".wav" => "audio/wav",
                ".ogg" or ".oga" or ".opus" => "audio/ogg",
                ".aac" => "audio/aac",
                ".m4a" or ".mp4" => "audio/mp4",
                ".flac" => "audio/flac",
                ".webm" => "audio/webm",
                _ => "audio/mpeg"
            };
            var name = WebUtility.UrlDecode(Path.GetFileName(path));
            return new CastMedia(url, type, string.IsNullOrEmpty(name) ? "RIoT2" : name);
        }

        // A command value arrives as JSON: "Sauna is ready", 40, true. Anything else is taken as text.
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
                Name = "Google Cast speaker",
                ClassFullName = GetType().FullName!,
                // The node adds the parameters from GoogleCastOptions.
                ReportTemplates = [],
                CommandTemplates =
                [
                    new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = SpeakAddress,
                        Name = "Speak",
                        Type = ValueType.Text,
                        Model = "Sauna is ready",
                        Schema = new ValueSchema { Type = "string", Semantic = ValueSemantics.Text, Title = "Text to speak", Description = $"At most {MaxTextLength} characters" }
                    },
                    new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = PlayUrlAddress,
                        Name = "Play media URL",
                        Type = ValueType.Text,
                        Model = "http://example.com/chime.mp3",
                        Schema = new ValueSchema { Type = "string", Title = "Media URL", Description = "An http or https URL of an audio file the speaker can download" }
                    },
                    new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = SetVolumeAddress,
                        Name = "Set volume",
                        Type = ValueType.Number,
                        Model = 40,
                        Schema = ValueSchema.Integer("%", null, 0, 100)
                    },
                    new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        Address = StopAddress,
                        Name = "Stop playback",
                        Type = ValueType.Boolean,
                        Model = true
                    }
                ]
            };
        }
    }
}
