// Ported from eufy-sdk src/transport/http/mega-client.ts, src/transport/http/passport.ts, src/client/device-registry.ts, src/transport/p2p/station-channels.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>
    /// The eufy "mega" (Anker AIoT) cloud HTTP client: algo_ecdh signed and encrypted transport, the login state
    /// machine (captcha, 2FA, persisted session), session recovery, the device list and the push/media helpers.
    /// </summary>
    /// <remarks>
    /// Login: estimate_domain on <c>mega-{region}.eufy.com</c> picks the region shard; one ECDH key exchange on
    /// <c>app-openapi-{region}.eufy.com</c> signs every later call; <c>/passport/login</c> carries the password
    /// encrypted against the server's static key; data calls go to <c>app-{service}-{region}.eufy.com</c>.
    /// </remarks>
    public sealed partial class EufyCloudClient : IEufyCloudApi
    {
        internal const string AppName = "eufy_mega";
        internal const string AppVersion = "6.0.41_26142";
        internal const string OsVersion = "36";

        /// <summary>Session kicked out (another login on the account). Arrives as HTTP 401.</summary>
        internal const int SessionKicked = 26084;
        /// <summary>"get identity error": the gateway no longer knows the key-ident; re-exchange once.</summary>
        internal const int IdentityKeyStale = 4404;
        /// <summary>Signature invalid: the wrong content-type was tried; retried on the other one.</summary>
        internal const int SignatureInvalid = 4416;
        /// <summary>Generic "try again" seen during the content-type probe.</summary>
        internal const int ProbeRetry = 10000;
        internal const int CaptchaRequired = 100032;
        internal const int CaptchaWrong = 100033;

        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan SessionKeyLifetime = TimeSpan.FromHours(12);
        /// <summary>First wait before replacing a token that was replaced recently; doubles per consecutive replacement.</summary>
        internal static readonly TimeSpan ReauthHoldOff = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan ReauthHoldOffCap = TimeSpan.FromMinutes(30);
        /// <summary>How long a replaced token must keep serving calls before the wait is forgotten.</summary>
        internal static readonly TimeSpan ReauthStable = TimeSpan.FromMinutes(10);

        internal const string ContendedSessionHint =
            "the token was rejected again soon after it was last replaced; one possible cause is another client signed " +
            "in with the same account and device identity, since the cloud holds about one session per pair and each " +
            "login ends the other's (where that is another install, give each its own openudid)";

        private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private readonly EufyAccountOptions _options;
        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly TimeProvider _time;
        private readonly IEufySessionStore _store;
        private readonly Lock _sync = new();
        private readonly AsyncLocal<bool> _insideReauth = new();

        private string _region = "us-pr";
        private string _estimatedDomain = "";
        private string? _bootstrapDomain;
        private EufySessionEntry? _sessionKey;
        /// <summary>Per-host ECDH keys of the eufylife gateways.</summary>
        private readonly Dictionary<string, EufySessionEntry> _sessionKeys = [];
        /// <summary>The in-flight key exchanges, by host and the token they carry.</summary>
        private readonly Dictionary<string, Task<EufySessionEntry>> _keyExchanges = [];
        private AuthState? _auth;
        private string? _pendingCaptchaId;
        private bool _pending2fa;
        private bool _reloginChallenged;
        private bool _loggingIn;
        private bool _rejectedTokenPending;
        private long _tokenExpiresAt;
        private readonly ConcurrentDictionary<string, string> _contentTypeByHost = new(StringComparer.Ordinal);
        private Task<bool>? _reauthAttempt;
        private int _recoveries;
        private DateTimeOffset _lastRecoveryAt;
        private readonly ConcurrentDictionary<string, string> _devicePublicKeys = new(StringComparer.Ordinal);
        private IReadOnlyList<EufyDeviceRecord> _devices = [];
        private bool _disposed;

        /// <param name="UserId">The login reply's <c>ap_cloud_user_id</c> where it has one.</param>
        /// <param name="AccountUserId">The account's own <c>user_id</c>; the gtoken header is hashed from it.</param>
        private sealed record AuthState(string UserId, string AuthToken, string? GeoKey, string? AccountUserId);

        private readonly record struct SignedRetry(bool Identity, bool Reauth);

        private enum SessionRecovery
        {
            Retry,
            Unrecoverable,
            HeldOff
        }

        private sealed record HttpReply(int Status, JsonElement? Json, string Text)
        {
            public JsonElement? Envelope => Json is { ValueKind: JsonValueKind.Object } e ? e : null;
            public int? Code => Envelope is { } e && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d) ? (int)d : null;
            public string? Msg => Envelope is { } e && e.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            public JsonElement? Data => Envelope is { } e && e.TryGetProperty("data", out var d) ? d : null;
            public string Describe(int max) => Truncate(Json is { } j ? j.GetRawText() : JsonSerializer.Serialize(Text), max);
        }

        public EufyCloudClient(EufyAccountOptions options, ILogger logger, HttpMessageHandler? handler = null)
            : this(options, logger, handler, null, null)
        {
        }

        internal EufyCloudClient(EufyAccountOptions options, ILogger logger, HttpMessageHandler? handler, IEufySessionStore? store, TimeProvider? time)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _logger = logger;
            _time = time ?? TimeProvider.System;
            _http = handler is null
                ? new HttpClient(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                }, disposeHandler: true)
                : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = Timeout.InfiniteTimeSpan;
            _store = store ?? FileEufySessionStore.ForAccount(options.DataDirectory, options.Email);

            // The device identity is generated once and persisted: a shifting identity looks like a new device and asks for 2FA.
            var saved = _store.Load();
            Openudid = saved?.Openudid ?? EufyCrypto.Md5Hex($"eufy-mega:{options.Email}")[..16];
            PhoneModel = saved?.PhoneModel ?? EufyPhoneModel.RandomPhoneModel(Openudid);
            MediaUserAgent = saved?.MediaUserAgent ?? EufyPhoneModel.RandomUserAgent(Openudid, PhoneModel);
            HydrateFromStore();
        }

        /// <summary>Stable per-install device id; the auth token is bound to it.</summary>
        internal string Openudid { get; }
        /// <summary>The phone model reported to the cloud.</summary>
        internal string PhoneModel { get; }
        /// <summary>The user agent of the media download path.</summary>
        internal string MediaUserAgent { get; }
        /// <summary>The DNS lookup the media download checks hosts with; replaced by tests.</summary>
        internal EufyMediaDownload.HostResolver HostResolver { get; set; } = EufyMediaDownload.DefaultResolver;
        /// <summary>Whether the last <see cref="GetDevicesAsync"/> lost at least one query (the list is then a subset).</summary>
        internal bool LastRefreshPartial { get; private set; }
        /// <summary>The active region shard (<c>eu-pr</c> / <c>us-pr</c>).</summary>
        internal string RegionShard => _region;

        private string CountryCode => string.IsNullOrWhiteSpace(_options.CountryCode) ? "US" : _options.CountryCode;
        private DateTimeOffset Now => _time.GetUtcNow();

        public bool IsAuthenticated
        {
            get
            {
                lock (_sync)
                    return _auth is not null && _sessionKey is not null && !_pending2fa;
            }
        }

        public string? UserId
        {
            get
            {
                lock (_sync)
                    return _auth?.UserId;
            }
        }

        /// <summary>The login email's local-part (the whole string when it has no <c>@</c>).</summary>
        public string AccountName
        {
            get
            {
                var email = (_options.Email ?? "").Trim();
                var at = email.IndexOf('@', StringComparison.Ordinal);
                return at > 0 ? email[..at] : email;
            }
        }

        /// <summary>
        /// Installs the stored session when it is usable and was logged in with the configured country; answers the
        /// adopted token. Read at construction and again while recovering from a rejection.
        /// </summary>
        private string? HydrateFromStore()
        {
            var saved = _store.Load();
            if (saved is null || !EufySessionValidity.IsSessionValid(saved, Now))
                return null;
            if (saved.CountryCode is not null && !string.Equals(saved.CountryCode, CountryCode, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("eufy: the stored session was logged in with country {Stored}, configured {Configured}; logging in again", saved.CountryCode, CountryCode);
                return null;
            }
            lock (_sync)
            {
                _region = saved.Region;
                _estimatedDomain = saved.EstimatedDomain ?? "";
                _auth = new AuthState(saved.UserId, saved.AuthToken, saved.GeoKey, saved.AccountUserId);
                _tokenExpiresAt = saved.TokenExpiresAt;
                _pending2fa = false;
                _reloginChallenged = false;
                _sessionKey = new EufySessionEntry(saved.KeyIdent, saved.ShareKey, "", "", Now);
            }
            _logger.LogDebug("eufy: restored the persisted session");
            return saved.AuthToken;
        }

        /// <summary>The headers every call carries; the dash/underscore duplicates are deliberate (the gateway reads a mix).</summary>
        private List<KeyValuePair<string, string>> BaseHeaders() =>
        [
            new("app-name", AppName),
            new("app-version", AppVersion),
            new("app_version", AppVersion),
            new("os-type", "android"),
            new("os_type", "android"),
            new("os-version", OsVersion),
            new("os_version", OsVersion),
            new("phone-model", PhoneModel),
            new("phone_model", PhoneModel),
            new("model-type", "PHONE"),
            new("country", CountryCode),
            new("ab_code", CountryCode),
            new("openudid", Openudid),
            new("language", "en"),
            new("test-flag", "false"),
            new("user-agent", "ktor-client"),
            new("accept", "application/json"),
            new("accept-charset", "UTF-8"),
        ];

        /// <summary>The id gtoken is hashed from: the account's own user_id.</summary>
        private static string GtokenUserId(AuthState auth) => auth.AccountUserId ?? auth.UserId;

        /// <summary><c>x-auth-token</c>, <c>authorization</c> and <c>gtoken</c>; nothing while logged out.</summary>
        internal Dictionary<string, string> AuthTokenHeaders()
        {
            AuthState? auth;
            lock (_sync)
                auth = _auth;
            if (auth is null)
                return [];
            return new Dictionary<string, string>
            {
                ["x-auth-token"] = auth.AuthToken,
                ["authorization"] = auth.AuthToken,
                ["gtoken"] = EufyCrypto.Gtoken(GtokenUserId(auth)),
            };
        }

        private static Dictionary<string, string> Merge(params IEnumerable<KeyValuePair<string, string>>[] parts)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                foreach (var (name, value) in part)
                    merged[name] = value;
            }
            return merged;
        }

        /// <summary>A POST that never throws on the HTTP status; the body is parsed as JSON when it is JSON.</summary>
        private async Task<HttpReply> HttpPostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase))
                    content.Headers.TryAddWithoutValidation("Content-Type", value);
                else
                    request.Headers.TryAddWithoutValidation(name, value);
            }
            request.Content = content;
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpReply> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RequestTimeout);
            var host = request.RequestUri?.Host ?? "";
            try
            {
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return new HttpReply((int)response.StatusCode, ParseMaybeJson(text), text);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EufyCloudException($"eufy cloud request to {host} timed out");
            }
            catch (HttpRequestException e)
            {
                throw new EufyCloudException($"eufy cloud request to {host} failed: {e.Message}", inner: e);
            }
        }

        private static JsonElement? ParseMaybeJson(string text)
        {
            if (text.Length == 0)
                return null;
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

        private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "undefined";

        /// <summary>Step 1: discovers the account's region shard (and the bootstrap host).</summary>
        internal async Task EstimateDomainAsync(CancellationToken cancellationToken)
        {
            var url = $"https://mega-{_region}.eufy.com/passport/estimate_domain";
            var body = JsonSerializer.Serialize(new { ab = CountryCode.ToLowerInvariant(), mode = 1 }, BodyJson);
            var reply = await HttpPostAsync(url, body, Merge(BaseHeaders(), [new("content-type", "application/json")]), cancellationToken).ConfigureAwait(false);
            var data = reply.Data is { ValueKind: JsonValueKind.Object } d ? d : (JsonElement?)null;
            var domain = "";
            if (data is { } obj)
            {
                foreach (var name in new[] { "domain", "host", "server_secret_info" })
                {
                    if (obj.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null)
                    {
                        domain = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                        break;
                    }
                }
            }
            var blob = data?.GetRawText() ?? "{}";
            lock (_sync)
            {
                _estimatedDomain = domain;
                if (blob.Contains("-eu-", StringComparison.Ordinal) || domain.Contains("-eu-", StringComparison.Ordinal))
                    _region = "eu-pr";
                else if (blob.Contains("-us-", StringComparison.Ordinal) || domain.Contains("-us-", StringComparison.Ordinal))
                    _region = "us-pr";
                if (domain.Length > 0 && !domain.StartsWith("mega-", StringComparison.Ordinal))
                    _bootstrapDomain = domain;
            }
            _logger.LogDebug("eufy: region {Region}, domain {Domain}", _region, domain);
        }

        /// <summary>
        /// Step 2: the key-exchange session of the host. One per region on the *.eufy.com stack (bound to the user at
        /// login, so it is reused, not re-exchanged), one per eufylife host. Concurrent callers share one exchange.
        /// </summary>
        internal async Task<EufySessionEntry> EnsureSessionKeyAsync(string? targetHost, CancellationToken cancellationToken)
        {
            var isEufylife = targetHost is not null && targetHost.Contains(".eufylife.com", StringComparison.Ordinal);
            Task<EufySessionEntry> exchange;
            TaskCompletionSource<EufySessionEntry>? started = null;
            string host;
            string flight;
            lock (_sync)
            {
                var now = Now;
                if (!isEufylife)
                {
                    if (_sessionKey is { } key && now - key.CreatedAt < SessionKeyLifetime)
                        return key;
                }
                else if (_sessionKeys.TryGetValue(targetHost!, out var cached) && now - cached.CreatedAt < SessionKeyLifetime)
                {
                    return cached;
                }
                host = isEufylife ? targetHost! : _bootstrapDomain ?? $"app-openapi-{_region}.eufy.com";
                // The token is part of the flight: an exchange started before a login is bound to no user.
                flight = $"{host}\n{_auth?.AuthToken ?? ""}";
                if (!_keyExchanges.TryGetValue(flight, out exchange!))
                {
                    started = new TaskCompletionSource<EufySessionEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
                    exchange = started.Task;
                    _keyExchanges[flight] = exchange;
                }
            }
            if (started is not null)
                _ = RunKeyExchangeAsync(host, isEufylife, flight, started);
            return await exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RunKeyExchangeAsync(string host, bool isEufylife, string flight, TaskCompletionSource<EufySessionEntry> completion)
        {
            EufySessionEntry? entry = null;
            Exception? error = null;
            try
            {
                entry = await ExchangeSessionKeyAsync(host, isEufylife, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
            }
            lock (_sync)
            {
                if (_keyExchanges.TryGetValue(flight, out var current) && current == completion.Task)
                    _keyExchanges.Remove(flight);
            }
            if (entry is not null)
                completion.TrySetResult(entry);
            else
                completion.TrySetException(error!);
        }

        /// <summary>One key exchange against <paramref name="host"/>, installed as that host's key.</summary>
        private async Task<EufySessionEntry> ExchangeSessionKeyAsync(string host, bool isEufylife, CancellationToken cancellationToken)
        {
            var path = isEufylife ? "/v3/openapi/oauth/key/exchange" : "/openapi/oauth/key/exchange";
            using var prep = EufyCrypto.PrepareKeyExchange(Now, isEufylife ? EufyCrypto.EufylifeLocalKeyHex : EufyCrypto.EufyMegaLocalKeyHex);
            var authHeaders = AuthTokenHeaders();
            var reply = await HttpPostAsync(
                $"https://{host}{path}",
                JsonSerializer.Serialize(new { client_public_key = prep.EncryptedClientPublicKey }, BodyJson),
                Merge(BaseHeaders(), prep.Headers, authHeaders, [new("content-type", "application/json")]),
                cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("eufy: key/exchange {Kind}({Auth}) -> {Status}/{Code}", isEufylife ? "eufylife " : "", authHeaders.Count > 0 ? "authed" : "anon", reply.Status, reply.Code);
            var serverKey = reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("server_public_key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()
                : null;
            if (reply.Status != 200 || reply.Code != 0 || string.IsNullOrEmpty(serverKey))
            {
                throw new EufyCloudException(
                    EufyCrypto.WithoutTokenEcho($"key/exchange failed ({reply.Status}/{Show(reply.Code)}): {reply.Msg ?? reply.Describe(160)}"),
                    reply.Code, reply.Status);
            }
            var entry = EufyCrypto.FinishKeyExchange(prep, serverKey, Now);
            lock (_sync)
            {
                if (isEufylife)
                    _sessionKeys[host] = entry;
                else
                    _sessionKey = entry;
            }
            return entry;
        }

        /// <summary>Whether a rejection's code or wording says the token is finished (paired with HTTP 401 by the caller).</summary>
        internal static bool TokenRejected(int? code, string? msg) => code == SessionKicked || TokenRejectedWording().IsMatch(msg ?? "");

        [GeneratedRegex("user_id is empty|invalid[^,]*\\btoken\\b|\\btoken\\b[^,]*(expired|error|not exist)|kicked|(?:\\btoken\\b|\\bsession\\b)[^,]*does not exist|unauthor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex TokenRejectedWording();

        [GeneratedRegex("get identity error|identity error", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex IdentityErrorWording();

        [GeneratedRegex("\\b100032\\b|\\b100033\\b", RegexOptions.CultureInvariant)]
        private static partial Regex CaptchaAny();

        [GeneratedRegex("\\b100033\\b", RegexOptions.CultureInvariant)]
        private static partial Regex CaptchaWrongAnswer();

        private static JsonElement Decode(string data, string shareKey)
        {
            var plaintext = Encoding.UTF8.GetString(EufyCrypto.DecryptBody(data, shareKey));
            return ParseMaybeJson(plaintext) ?? JsonSerializer.SerializeToElement(plaintext);
        }

        /// <summary>
        /// Signed and encrypted POST. Probes the per-host content-type (4416/10000/400 retry on the other one), re-exchanges
        /// a stale key once (4404), replaces a rejected token once by logging in again, and classifies what is left.
        /// </summary>
        private async Task<JsonElement> PostSignedAsync(string host, string path, object? body, bool authed, SignedRetry retry, CancellationToken cancellationToken)
        {
            var entry = await EnsureSessionKeyAsync(host, cancellationToken).ConfigureAwait(false);
            string? sentToken;
            lock (_sync)
                sentToken = authed ? _auth?.AuthToken : null;
            var encBody = EufyCrypto.EncryptBody(JsonSerializer.Serialize(body ?? new Dictionary<string, object?>(), BodyJson), entry.ShareKey);
            var wantsText = path.Contains("/passport/", StringComparison.Ordinal) || host.Contains(".eufylife.com", StringComparison.Ordinal);
            var preferred = _contentTypeByHost.TryGetValue(host, out var cached) ? cached : wantsText ? "text/plain" : "application/json";
            string[] order = preferred == "text/plain" ? ["text/plain", "application/json"] : ["application/json", "text/plain"];

            (int? Code, string Msg, int Status)? last = null;
            foreach (var contentType in order)
            {
                var headers = Merge(
                    BaseHeaders(),
                    [new("content-type", contentType)],
                    EufyCrypto.SignedHeaders(entry, encBody, Now),
                    [new("x-replay-info", "replay")],
                    authed ? AuthTokenHeaders() : new Dictionary<string, string>());
                var reply = await HttpPostAsync($"https://{host}{path}", encBody, headers, cancellationToken).ConfigureAwait(false);
                var code = reply.Code;
                if (reply.Status == 200 && code == 0)
                {
                    if (authed)
                        NoteSessionWorking();
                    _contentTypeByHost[host] = contentType;
                    var data = reply.Data;
                    if (data is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } cipher)
                        return Decode(cipher, entry.ShareKey);
                    return data ?? default;
                }
                // A captcha challenge arrives as an encrypted data field beside a non-zero code.
                var detail = reply.Msg ?? reply.Describe(200);
                if (reply.Data is { ValueKind: JsonValueKind.String } errData && errData.GetString() is { Length: > 0 } errCipher)
                {
                    try
                    {
                        detail = $"{detail} | data={Truncate(Encoding.UTF8.GetString(EufyCrypto.DecryptBody(errCipher, entry.ShareKey)), 300)}";
                    }
                    catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
                    {
                        // not decryptable
                    }
                }
                last = (code, EufyCrypto.WithoutTokenEcho(detail), reply.Status);
                var retryable = (reply.Status == 403 && code == SignatureInvalid) || code == ProbeRetry || reply.Status == 400;
                if (code != 0 && !retryable)
                    _logger.LogDebug("eufy: {Path} answered {Status}/{Code}", path, reply.Status, Show(code));
                if (!retryable)
                    break;
            }

            var (lastCode, lastMsg, lastStatus) = last!.Value;
            var identityError = authed && (lastCode == IdentityKeyStale || IdentityErrorWording().IsMatch(lastMsg));
            bool hasAuth;
            lock (_sync)
                hasAuth = _auth is not null;
            if (identityError && !retry.Identity && hasAuth)
            {
                _logger.LogDebug("eufy: identity error, re-exchanging the session key and retrying");
                lock (_sync)
                {
                    if (host.Contains(".eufylife.com", StringComparison.Ordinal))
                    {
                        if (_sessionKeys.TryGetValue(host, out var current) && current == entry)
                            _sessionKeys.Remove(host);
                    }
                    else if (_sessionKey == entry)
                    {
                        _sessionKey = null;
                    }
                }
                try
                {
                    await EnsureSessionKeyAsync(host, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ClearSession();
                    throw new EufySessionExpiredException($"{path} failed (key re-exchange rejected): {lastMsg}");
                }
                Persist();
                return await PostSignedAsync(host, path, body, authed, retry with { Identity = true }, cancellationToken).ConfigureAwait(false);
            }

            // Only a fresh key-ident that was still rejected, or a 401 whose code/wording says so, drops the session.
            var tokenFinished = (identityError && retry.Identity) || (authed && lastStatus == 401 && TokenRejected(lastCode, lastMsg));
            if (tokenFinished)
            {
                var reason = $"{path} failed ({lastStatus}/{Show(lastCode)}): {lastMsg}";
                lock (_sync)
                    _rejectedTokenPending = true;
                var recovery = retry.Reauth ? SessionRecovery.Unrecoverable : await RecoverRejectedSessionAsync(sentToken).ConfigureAwait(false);
                if (recovery == SessionRecovery.Retry)
                    return await PostSignedAsync(host, path, body, authed, retry with { Reauth = true }, cancellationToken).ConfigureAwait(false);
                if (!SessionReplacedSince(sentToken))
                    ClearSession();
                var contended = recovery == SessionRecovery.HeldOff;
                throw new EufySessionExpiredException(contended ? $"{reason} — {ContendedSessionHint}" : reason, HoldOffRemaining());
            }
            throw new EufyCloudException($"{path} failed ({lastStatus}/{Show(lastCode)}): {lastMsg}", lastCode, lastStatus);
        }

        /// <summary>Whether the session the call was made against has already been replaced by another call's recovery.</summary>
        private bool SessionReplacedSince(string? sentToken)
        {
            lock (_sync)
                return _auth is { } auth && auth.AuthToken != sentToken;
        }

        /// <summary>
        /// Deals with a rejection that means the token is finished: use a session another call already obtained, join a
        /// recovery in flight, adopt a session a shared store holds, and only then spend a login, rate-bounded.
        /// </summary>
        private async Task<SessionRecovery> RecoverRejectedSessionAsync(string? sentToken)
        {
            Task<bool>? attempt;
            lock (_sync)
            {
                if (SessionReplacedSince(sentToken))
                    return SessionRecovery.Retry;
                if (_reauthAttempt is { } joining)
                {
                    // A call made by the re-login itself cannot wait for that re-login.
                    if (_insideReauth.Value)
                        return SessionRecovery.Unrecoverable;
                    attempt = joining;
                }
                else
                {
                    if (HydrateFromStore() is not null && SessionReplacedSince(sentToken))
                        return SessionRecovery.Retry;
                    if (!CanReauthenticate())
                        return SessionRecovery.Unrecoverable;
                    if (!RecoveryDue())
                        return SessionRecovery.HeldOff;
                    NoteTokenReplacement();
                    ClearSession();
                    attempt = StartReauthentication();
                }
            }
            return await attempt.ConfigureAwait(false) ? SessionRecovery.Retry : SessionRecovery.Unrecoverable;
        }

        /// <summary>Whether a token may be replaced now, given how recently the last one was.</summary>
        private bool RecoveryDue()
        {
            var remaining = HoldOffRemaining();
            if (remaining == TimeSpan.Zero)
                return true;
            _logger.LogWarning("eufy: holding off {Remaining}s more before replacing the token again (last replaced {Ago}s ago): {Hint}",
                Math.Round(remaining.TotalSeconds), Math.Round((Now - _lastRecoveryAt).TotalSeconds), ContendedSessionHint);
            return false;
        }

        /// <summary>How much longer a token replacement must wait; zero when one may run now.</summary>
        internal TimeSpan HoldOffRemaining()
        {
            lock (_sync)
            {
                if (_recoveries == 0)
                    return TimeSpan.Zero;
                var wait = TimeSpan.FromTicks(Math.Min(ReauthHoldOff.Ticks * (1L << Math.Min(_recoveries - 1, 30)), ReauthHoldOffCap.Ticks));
                var remaining = wait - (Now - _lastRecoveryAt);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        /// <summary>Counts one token replacement against the hold-off and clears the rejection it answered.</summary>
        private void NoteTokenReplacement()
        {
            lock (_sync)
            {
                _recoveries++;
                _lastRecoveryAt = Now;
                _rejectedTokenPending = false;
            }
        }

        /// <summary>The held session works; a replacement that keeps working long enough forgets the hold-off.</summary>
        private void NoteSessionWorking()
        {
            lock (_sync)
            {
                _rejectedTokenPending = false;
                if (_recoveries > 0 && Now - _lastRecoveryAt > ReauthStable)
                    _recoveries = 0;
            }
        }

        /// <summary>Not from inside a login, not while a 2FA code is outstanding, not after a challenged re-login, not without credentials.</summary>
        private bool CanReauthenticate() =>
            !string.IsNullOrEmpty(_options.Email) && !string.IsNullOrEmpty(_options.Password) && !_loggingIn && !_pending2fa && !_reloginChallenged;

        /// <summary>One shared re-login for every call rejected on the same dead token. Call under the lock.</summary>
        private Task<bool> StartReauthentication()
        {
            if (_reauthAttempt is { } running)
                return running;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reauthAttempt = completion.Task;
            _ = Task.Run(async () =>
            {
                _insideReauth.Value = true;
                var ok = false;
                try
                {
                    var result = await LoginAsync(CancellationToken.None).ConfigureAwait(false);
                    ok = result.Status == EufyLoginStatus.Ok;
                    if (ok)
                    {
                        _logger.LogDebug("eufy: the token was rejected; logged in again");
                    }
                    else
                    {
                        lock (_sync)
                            _reloginChallenged = true;
                        _logger.LogWarning("eufy: the token was rejected and the re-login needs {Status}; cannot recover", result.Status);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning("eufy: re-login failed: {Message}", EufyCrypto.WithoutTokenEcho(e.Message));
                }
                lock (_sync)
                {
                    if (_reauthAttempt == completion.Task)
                        _reauthAttempt = null;
                }
                completion.TrySetResult(ok);
            });
            return completion.Task;
        }

        public async Task<EufyLoginResult> LoginAsync(CancellationToken cancellationToken)
        {
            // A restored session is reused without a round trip; not while a 2FA code is outstanding.
            lock (_sync)
            {
                if (_auth is not null && _sessionKey is not null && !_pending2fa)
                    return new EufyLoginResult(EufyLoginStatus.Ok);
            }
            return await AttemptLoginAsync(LoginBody(), 2, cancellationToken).ConfigureAwait(false);
        }

        public async Task<EufyLoginResult> SolveCaptchaAsync(string answer, CancellationToken cancellationToken)
        {
            string? captchaId;
            lock (_sync)
                captchaId = _pendingCaptchaId;
            if (captchaId is null)
                throw new InvalidOperationException("no pending captcha; call LoginAsync first");
            return await AttemptLoginAsync(LoginBody(captchaId: captchaId, answer: answer), 2, cancellationToken).ConfigureAwait(false);
        }

        public Task<EufyLoginResult> SubmitVerifyCodeAsync(string code, CancellationToken cancellationToken) =>
            AttemptLoginAsync(LoginBody(verifyCode: code), 2, cancellationToken);

        /// <summary>The /passport/login body: the credentials, then the challenge fields of the step.</summary>
        private Dictionary<string, object?> LoginBody(string? verifyCode = null, string? captchaId = null, string? answer = null)
        {
            var (publicKey, encryptedPassword) = EufyCrypto.EncryptLoginPassword(_options.Password);
            var body = new Dictionary<string, object?>
            {
                ["email"] = _options.Email,
                ["password"] = encryptedPassword,
                ["ab"] = CountryCode,
                ["client_secret_info"] = new Dictionary<string, object?> { ["public_key"] = publicKey },
            };
            if (!string.IsNullOrEmpty(verifyCode))
            {
                body["verify_code"] = verifyCode;
                body["login_id"] = "";
            }
            else if (!string.IsNullOrEmpty(captchaId))
            {
                body["answer"] = answer ?? "";
                body["captcha_id"] = captchaId;
                body["verify_code"] = "";
                body["login_id"] = "";
            }
            else
            {
                body["answer"] = "";
                body["captcha_id"] = "";
                body["verify_code"] = "";
                body["login_id"] = "";
            }
            return body;
        }

        private async Task<EufyLoginResult> AttemptLoginAsync(Dictionary<string, object?> body, int messageType, CancellationToken cancellationToken)
        {
            lock (_sync)
                _loggingIn = true;
            try
            {
                return await LoginRoundTripAsync(body, messageType, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                    _loggingIn = false;
            }
        }

        private async Task<EufyLoginResult> LoginRoundTripAsync(Dictionary<string, object?> body, int messageType, CancellationToken cancellationToken)
        {
            await EstimateDomainAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSessionKeyAsync(null, cancellationToken).ConfigureAwait(false);
            var passportHost = $"app-passport-{_region}.eufy.com";
            var isVerify = body.TryGetValue("verify_code", out var verify) && verify is string { Length: > 0 };

            JsonElement res;
            try
            {
                bool authed;
                lock (_sync)
                    authed = _auth is not null;
                res = await PostSignedAsync(passportHost, "/passport/login", body, authed, default, cancellationToken).ConfigureAwait(false);
            }
            catch (EufyCloudException e) when (CaptchaAny().IsMatch(e.Message))
            {
                var (captchaId, image) = await GenerateCaptchaAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                    _pendingCaptchaId = captchaId;
                return new EufyLoginResult(EufyLoginStatus.Captcha, image, CaptchaWrongAnswer().IsMatch(e.Message));
            }

            if (res.ValueKind == JsonValueKind.Object)
                _logger.LogDebug("eufy: login reply keys {Keys}", string.Join(",", res.EnumerateObject().Select(p => p.Name)));
            var reply = ReadLoginReply(res) ?? throw new EufyCloudException("login returned no session");

            if (!isVerify && reply.TwoFactorPending)
            {
                // The limited token authenticates the verify-code send and the verify-login that follows.
                lock (_sync)
                {
                    _auth = new AuthState(reply.UserId, reply.AuthToken, reply.GeoKey, reply.AccountUserId);
                    _pendingCaptchaId = null;
                    _pending2fa = true;
                }
                await SendVerifyCodeAsync(messageType, cancellationToken).ConfigureAwait(false);
                return new EufyLoginResult(EufyLoginStatus.TwoFactor, TwoFactorMethod: "code sent via app-push/sendmsg/verify_code");
            }

            lock (_sync)
            {
                _pendingCaptchaId = null;
                _pending2fa = false;
                _reloginChallenged = false;
                _auth = new AuthState(reply.UserId, reply.AuthToken, reply.GeoKey, reply.AccountUserId);
                _tokenExpiresAt = reply.TokenExpiresAt;
                // Re-exchange with the auth token so the gateway binds the key-ident to the user.
                _sessionKey = null;
            }
            await EnsureSessionKeyAsync(null, cancellationToken).ConfigureAwait(false);
            Persist();
            bool pending;
            lock (_sync)
                pending = _rejectedTokenPending;
            if (pending)
                NoteTokenReplacement();
            return new EufyLoginResult(EufyLoginStatus.Ok);
        }

        internal sealed record LoginReply(string UserId, string? AccountUserId, string AuthToken, string? GeoKey, long TokenExpiresAt, bool TwoFactorPending);

        /// <summary>Reads a decrypted /passport/login reply; null when it carries no id or no token.</summary>
        internal static LoginReply? ReadLoginReply(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object)
                return null;
            var userId = FirstPresent(data, "ap_cloud_user_id", "user_id", "userId");
            var authToken = FirstPresent(data, "auth_token", "token");
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(authToken))
                return null;
            long expires = 0;
            if (data.TryGetProperty("token_expires_at", out var e))
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n))
                    expires = (long)n;
                else if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
                    expires = (long)parsed;
            }
            var twoFactor = data.TryGetProperty("fa_info", out var fa) && fa.ValueKind == JsonValueKind.Object && fa.TryGetProperty("info", out var info) && Truthy(info);
            return new LoginReply(userId, FirstPresent(data, "user_id", "userId"), authToken, StringOf(data, "geo_key"), expires, twoFactor);
        }

        /// <summary>The first of <paramref name="names"/> that is present and not null (JavaScript's <c>??</c> chain), as a string.</summary>
        private static string? FirstPresent(JsonElement data, params string[] names)
        {
            foreach (var name in names)
            {
                if (data.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            }
            return null;
        }

        private static string? StringOf(JsonElement data, string name) =>
            data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool Truthy(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Number => value.TryGetDouble(out var d) && d != 0 && !double.IsNaN(d),
            JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };

        /// <summary>A fresh captcha challenge: its id and a <c>data:image/png;base64,</c> image.</summary>
        private async Task<(string CaptchaId, string Image)> GenerateCaptchaAsync(CancellationToken cancellationToken)
        {
            var r = await PostSignedAsync($"app-passport-{_region}.eufy.com", "/passport/generate/captcha", new Dictionary<string, object?>(), false, default, cancellationToken).ConfigureAwait(false);
            return (StringOf(r, "captcha_id") ?? "", StringOf(r, "item") ?? "");
        }

        /// <summary>Triggers the 2FA verify code (biz_type 1004; message_type 2 = email, 1 = SMS) with the limited token.</summary>
        private Task SendVerifyCodeAsync(int messageType, CancellationToken cancellationToken) =>
            PostSignedAsync($"app-push-{_region}.eufy.com", "/app/sendmsg/verify_code",
                new { biz_type = 1004, message_type = messageType, transaction = Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
                true, default, cancellationToken);

        /// <summary>Saves the token and the session key for reuse across runs.</summary>
        private void Persist()
        {
            PersistedSession session;
            lock (_sync)
            {
                if (_auth is null || _sessionKey is null)
                    return;
                session = new PersistedSession
                {
                    UserId = _auth.UserId,
                    AccountUserId = _auth.AccountUserId ?? _auth.UserId,
                    AuthToken = _auth.AuthToken,
                    GeoKey = _auth.GeoKey,
                    Region = _region,
                    CountryCode = CountryCode,
                    EstimatedDomain = _estimatedDomain,
                    Openudid = Openudid,
                    PhoneModel = PhoneModel,
                    MediaUserAgent = MediaUserAgent,
                    ShareKey = _sessionKey.ShareKey,
                    KeyIdent = _sessionKey.KeyIdent,
                    TokenExpiresAt = _tokenExpiresAt,
                    SavedAt = Now.ToUnixTimeMilliseconds(),
                };
            }
            try
            {
                _store.Save(session);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("eufy: could not persist the session: {Message}", e.Message);
            }
        }

        public void ClearSession()
        {
            lock (_sync)
            {
                _auth = null;
                _sessionKey = null;
                _pendingCaptchaId = null;
                _pending2fa = false;
            }
            _store.Clear();
        }

        public Task<JsonElement> PostAsync(string service, string path, object? body, CancellationToken cancellationToken) =>
            PostSignedAsync($"app-{service}-{_region}.eufy.com", path, body ?? new Dictionary<string, object?>(), true, default, cancellationToken);

        /// <summary>The security-app (eufylife) data host of the region; the US one has no suffix.</summary>
        internal string SecurityAppHost()
        {
            var shard = _region.Split('-')[0];
            return shard == "us" ? "security-app.eufylife.com" : $"security-app-{shard}.eufylife.com";
        }

        public Task<JsonElement> SecurityAppPostAsync(string path, object? body, CancellationToken cancellationToken) =>
            PostSignedAsync(SecurityAppHost(), path, body ?? new Dictionary<string, object?>(), true, default, cancellationToken);

        public async Task<IReadOnlyDictionary<string, (string DskKey, long Expiration)>> GetDskKeysAsync(IReadOnlyCollection<string> stationSerials, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stationSerials);
            // The server keys off a device_dsks array; invalid_dsk is the prior key being rotated out (empty on a first fetch).
            var invalid = new Dictionary<string, string>();
            var deviceDsks = new List<Dictionary<string, string>>();
            foreach (var sn in stationSerials)
            {
                invalid[sn] = "";
                deviceDsks.Add(new Dictionary<string, string> { ["invalid_dsk"] = "", ["device_sn"] = sn, ["category"] = "eufy_security" });
            }
            var res = await PostAsync("devicerelation", "/app/devicerelation/get_dsk_keys",
                new { device_dsks = deviceDsks, invalid_dsks = invalid, station_sns = stationSerials.ToArray() }, cancellationToken).ConfigureAwait(false);
            var output = new Dictionary<string, (string, long)>();
            if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("device_dsks", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in list.EnumerateArray())
                {
                    var sn = StringOf(k, "device_sn");
                    if (sn is null)
                        continue;
                    long expiration = 0;
                    if (k.TryGetProperty("expiration", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetDouble(out var e))
                        expiration = (long)e;
                    output[sn] = (StringOf(k, "dsk_key") ?? "", expiration);
                }
            }
            return output;
        }

        public async Task<string> GetDevicePublicKeyAsync(string deviceSerial, CancellationToken cancellationToken)
        {
            if (_devicePublicKeys.TryGetValue(deviceSerial, out var cached))
                return cached;
            var url = $"https://{SecurityAppHost()}/v1/app/public_key/query?device_sn={Uri.EscapeDataString(deviceSerial)}&type=2";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in Merge(BaseHeaders(), AuthTokenHeaders()))
                request.Headers.TryAddWithoutValidation(name, value);
            var reply = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var key = reply.Data is { ValueKind: JsonValueKind.Object } d ? StringOf(d, "public_key") : null;
            if (reply.Status != 200 || reply.Code != 0 || string.IsNullOrEmpty(key))
                throw new EufyCloudException($"public_key/query failed ({reply.Status}/{Show(reply.Code)}): {reply.Msg ?? "no public key"}", reply.Code, reply.Status);
            _devicePublicKeys[deviceSerial] = key;
            return key;
        }

        public async Task RegisterPushTokenAsync(string fcmToken, CancellationToken cancellationToken)
        {
            await PostAsync("push", "/app/push/register_push_token",
                new { is_notification_enable = true, token = fcmToken, voip_token = "" }, cancellationToken).ConfigureAwait(false);
            await SecurityAppPostAsync("/v1/apppush/register_push_token",
                new { is_notification_enable = true, token = fcmToken, transaction = Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
            await SecurityAppPostAsync("/v1/app/review/app_push_check",
                new { app_type = "eufySecurity", transaction = Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Downloads raw bytes from a push-media URL with the active session.</summary>
        internal async Task<byte[]> DownloadMediaAsync(string url, CancellationToken cancellationToken)
        {
            AuthState? auth;
            lock (_sync)
                auth = _auth;
            if (auth is null)
                throw new InvalidOperationException("LoginAsync first");
            var headers = new Dictionary<string, string>
            {
                ["x-auth-token"] = auth.AuthToken,
                ["gtoken"] = EufyCrypto.Gtoken(GtokenUserId(auth)),
                ["app-name"] = AppName,
                ["model-type"] = "PHONE",
                ["user-agent"] = MediaUserAgent,
            };
            try
            {
                return await EufyMediaDownload.DownloadAsync(_http, url, headers, HostResolver, cancellationToken).ConfigureAwait(false);
            }
            catch (EufyMediaAuthenticationException)
            {
                throw new EufySessionExpiredException("media download rejected the active session");
            }
        }

        public async Task<byte[]> DownloadImageAsync(string url, string? p2pDid, CancellationToken cancellationToken)
        {
            var data = await DownloadMediaAsync(url, cancellationToken).ConfigureAwait(false);
            try
            {
                return PushImageDecoder.NormalizePushImage(data, p2pDid);
            }
            catch (Exception e)
            {
                // A decoder refusing bytes that did arrive is a key problem, not a network one.
                throw new EufyMediaException("Push image could not be decoded", "decode-failed", inner: e);
            }
        }

        /// <summary>
        /// Every station and device of the account. <c>get_devs_list</c> is house-scoped (the bare call and the per-house
        /// calls may differ), so it is queried for <c>{}</c> and each house, merged by serial. A failing query keeps the
        /// devices it would have listed from the previous result; a rejected session is rethrown.
        /// </summary>
        public async Task<IReadOnlyList<EufyDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            var bodies = new List<Dictionary<string, object?>> { new() };
            var partial = false;
            try
            {
                var houses = await PostAsync("house", "/app/house/get_house_list", null, cancellationToken).ConfigureAwait(false);
                if (houses.ValueKind == JsonValueKind.Object && houses.TryGetProperty("house_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in infos.EnumerateArray())
                    {
                        if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("house_id", out var id))
                            bodies.Add(new Dictionary<string, object?> { ["house_id"] = id.Clone() });
                    }
                }
            }
            catch (Exception e) when (e is not EufySessionExpiredException && !(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                partial = true;
                _logger.LogWarning("eufy: the house list could not be read: {Message}", EufyCrypto.WithoutTokenEcho(e.Message));
            }

            var seen = new Dictionary<string, EufyDeviceRecord>(StringComparer.Ordinal);
            foreach (var body in bodies)
            {
                JsonElement res;
                try
                {
                    res = await PostAsync("house", "/app/house/get_devs_list", body, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not EufySessionExpiredException && !(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    partial = true;
                    _logger.LogWarning("eufy: a device list query failed: {Message}", EufyCrypto.WithoutTokenEcho(e.Message));
                    continue;
                }
                if (res.ValueKind != JsonValueKind.Object || !res.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var raw in devices.EnumerateArray())
                {
                    if (ToRecord(raw) is { } record)
                        seen[record.Serial] = record;
                }
            }
            LastRefreshPartial = partial;
            if (partial)
            {
                foreach (var previous in _devices)
                    seen.TryAdd(previous.Serial, previous);
            }
            _devices = [.. seen.Values];
            return _devices;
        }

        /// <summary>Maps one <c>get_devs_list</c> entry; null when it has no serial.</summary>
        internal static EufyDeviceRecord? ToRecord(JsonElement raw)
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return null;
            var serial = StringOf(raw, "device_sn");
            if (string.IsNullOrEmpty(serial))
                return null;
            var name = RecordName(raw);
            return new EufyDeviceRecord
            {
                Serial = serial,
                Name = string.IsNullOrEmpty(name) ? serial : name,
                Model = StringOf(raw, "device_model"),
                StationSerial = ResolvedStationSn(raw, serial),
                Channel = IntOf(raw, "device_channel"),
                P2pDid = StringOf(raw, "p2p_did"),
                DeviceType = IntOf(raw, "device_type"),
                Params = MergeParams(raw),
                Raw = raw.Clone(),
            };
        }

        /// <summary><c>device_name</c> first, then the alias names (the non-vacuum order; device classes are not resolved here).</summary>
        private static string? RecordName(JsonElement raw)
        {
            foreach (var name in new[] { "device_name", "device_alias_name", "alias_name" })
            {
                if (raw.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            }
            return null;
        }

        /// <summary>The station a device's commands go through: a populated <c>parent_sn</c>, else <c>station_sn</c>, else itself.</summary>
        internal static string ResolvedStationSn(JsonElement raw, string sn)
        {
            var parent = StringOf(raw, "parent_sn");
            if (!string.IsNullOrEmpty(parent) && parent != sn)
                return parent;
            var station = StringOf(raw, "station_sn");
            return string.IsNullOrEmpty(station) ? sn : station;
        }

        private static int? IntOf(JsonElement raw, string name) =>
            raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

        /// <summary>The <c>params</c> list as param_type → param_value (as a string).</summary>
        internal static Dictionary<int, string> MergeParams(JsonElement raw)
        {
            var output = new Dictionary<int, string>();
            if (!raw.TryGetProperty("params", out var list) || list.ValueKind != JsonValueKind.Array)
                return output;
            foreach (var p in list.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object
                    || !p.TryGetProperty("param_type", out var type) || type.ValueKind is JsonValueKind.Null
                    || !p.TryGetProperty("param_value", out var value) || value.ValueKind is JsonValueKind.Null)
                {
                    continue;
                }
                int key;
                if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var n))
                    key = n;
                else if (type.ValueKind == JsonValueKind.String && int.TryParse(type.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    key = parsed;
                else
                    continue;
                output[key] = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.GetRawText(),
                };
            }
            return output;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _http.Dispose();
        }
    }
}
