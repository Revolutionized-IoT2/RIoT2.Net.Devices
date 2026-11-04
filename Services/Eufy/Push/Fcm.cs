// Ported from eufy-sdk src/transport/push/fcm.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>
    /// The Firebase project constants upstream read from the v6 eufy Security APK
    /// (com.oceanwing.battery.cam): project batterycam-3250a, sender 348804314802.
    /// </summary>
    internal static class FcmConstants
    {
        public const string ProjectId = "batterycam-3250a";
        public const string ApiKey = "AIzaSyCSz1uxGrHXsEktm7O3_wv-uLGpC9BvXR8";
        public const string AppId = "1:348804314802:android:440a6773b3620da7";
        public const string SenderId = "348804314802";
        public const string Package = "com.oceanwing.battery.cam";
        public const string CertSha1 = "F051262F9F99B638F3C76DE349830638555B4A0A";
        public const string AuthVersion = "FIS_v2";
        public const string SdkVersion = "a:16.3.1";

        public const string InstallationsUrl = "https://firebaseinstallations.googleapis.com/v1/projects/" + ProjectId + "/installations";
        public const string CheckinUrl = "https://android.clients.google.com/checkin";
        public const string RegisterUrl = "https://android.clients.google.com/c2dm/register3";
    }

    /// <summary>
    /// FCM registration: generate a Firebase Installation ID (FID), register it with Firebase installations,
    /// GServices check-in (androidId + securityToken), then GCM register3 (the FCM token).
    /// </summary>
    internal sealed partial class FcmRegistrar
    {
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public FcmRegistrar(HttpClient http, ILogger logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _http = http;
            _logger = logger;
            _delay = delay ?? Task.Delay;
        }

        /// <summary>A valid Firebase Installation ID: 22 url-safe base64 characters with the 4-bit FID header 0111.</summary>
        public static string GenerateFid()
        {
            var b = RandomNumberGenerator.GetBytes(17);
            b[0] = (byte)(0b01110000 + (b[0] % 0b00010000));
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..22];
        }

        /// <summary>Full registration.</summary>
        public async Task<FcmCredentials> RegisterAsync(CancellationToken cancellationToken)
        {
            var fid = GenerateFid();
            var (refreshToken, authToken) = await InstallFidAsync(fid, cancellationToken).ConfigureAwait(false);
            var (androidId, securityToken) = await CheckinAsync(cancellationToken).ConfigureAwait(false);
            var fcmToken = await GcmRegisterAsync(fid, androidId, securityToken, authToken, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("[fcm] registered a push token");
            return new FcmCredentials
            {
                Fid = fid,
                AndroidId = androidId,
                SecurityToken = securityToken,
                FcmToken = fcmToken,
                RefreshToken = refreshToken,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        private async Task<(string? RefreshToken, string AuthToken)> InstallFidAsync(string fid, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, FcmConstants.InstallationsUrl);
            request.Headers.TryAddWithoutValidation("X-Android-Package", FcmConstants.Package);
            request.Headers.TryAddWithoutValidation("X-Android-Cert", FcmConstants.CertSha1);
            request.Headers.TryAddWithoutValidation("x-goog-api-key", FcmConstants.ApiKey);
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["fid"] = fid,
                ["appId"] = FcmConstants.AppId,
                ["authVersion"] = FcmConstants.AuthVersion,
                ["sdkVersion"] = FcmConstants.SdkVersion,
            });
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
                throw new HttpRequestException($"FID install failed ({(int)response.StatusCode}): {Truncate(text, 200)}", null, response.StatusCode);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            string? refreshToken = root.TryGetProperty("refreshToken", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (!root.TryGetProperty("authToken", out var auth) || auth.ValueKind != JsonValueKind.Object
                || !auth.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("FID install returned no authToken");
            return (refreshToken, token.GetString()!);
        }

        internal static byte[] BuildCheckinRequest() => new CheckinRequest
        {
            AndroidId = 0,
            Build = new CheckinBuild
            {
                Fingerprint = "google/razor/flo:5.0.1/LRX22C/1602158:user/release-keys",
                Hardware = "flo",
                Brand = "google",
                Radio = "FLO-04.04",
                ClientId = "android-google",
            },
            LastCheckinMs = 0,
            Locale = "en",
            LoggingId = 1234567890,
            TimeZone = "GMT",
            Version = 3,
            Fragment = 0,
            UserSerialNumber = 0,
        }.Encode();

        private async Task<(string AndroidId, string SecurityToken)> CheckinAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, FcmConstants.CheckinUrl);
            request.Content = new ByteArrayContent(BuildCheckinRequest());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
                throw new HttpRequestException($"checkin failed ({(int)response.StatusCode})", null, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var decoded = CheckinResponse.Decode(bytes);
            if (decoded.AndroidId is not { } androidId || androidId == 0 || decoded.SecurityToken is not { } securityToken || securityToken == 0)
                throw new InvalidDataException("checkin returned no androidId/securityToken");
            return (androidId.ToString(CultureInfo.InvariantCulture), securityToken.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The register3 form, in upstream's order.</summary>
        internal static IReadOnlyList<KeyValuePair<string, string>> BuildRegisterForm(string fid, string androidId, string fidAuthToken) =>
        [
            new("X-subtype", FcmConstants.SenderId),
            new("sender", FcmConstants.SenderId),
            new("X-app_ver", "741"),
            new("X-osv", "25"),
            new("X-cliv", "fiid-20.2.0"),
            new("X-gmsv", "201216023"),
            new("X-appid", fid),
            new("X-scope", "*"),
            new("X-Goog-Firebase-Installations-Auth", fidAuthToken),
            new("X-gmp_app_id", FcmConstants.AppId),
            new("X-firebase-app-name-hash", "R1dAH9Ui7M-ynoznwBdw01tLxhI"),
            new("X-Firebase-Client-Log-Type", "1"),
            new("app", FcmConstants.Package),
            new("device", androidId),
            new("app_ver", "741"),
            new("gcm_ver", "201216023"),
            new("plat", "0"),
            new("cert", FcmConstants.CertSha1),
            new("target_ver", "28"),
        ];

        /// <summary>Serialises like WHATWG <c>URLSearchParams.toString()</c> (space as '+', <c>*-._</c> kept).</summary>
        internal static string FormEncode(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var sb = new StringBuilder();
            foreach (var (key, value) in pairs)
            {
                if (sb.Length > 0) sb.Append('&');
                AppendEncoded(sb, key);
                sb.Append('=');
                AppendEncoded(sb, value);
            }
            return sb.ToString();
        }

        private static void AppendEncoded(StringBuilder sb, string value)
        {
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if (char.IsAsciiLetterOrDigit(c) || c is '*' or '-' or '.' or '_') sb.Append(c);
                else if (c == ' ') sb.Append('+');
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        private async Task<string> GcmRegisterAsync(string fid, string androidId, string securityToken, string fidAuthToken, CancellationToken cancellationToken)
        {
            var body = FormEncode(BuildRegisterForm(fid, androidId, fidAuthToken));
            // The gateway is occasionally flaky; retry a few times.
            var last = "";
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, FcmConstants.RegisterUrl);
                request.Headers.TryAddWithoutValidation("Authorization", $"AidLogin {androidId}:{securityToken}");
                request.Headers.TryAddWithoutValidation("app", FcmConstants.Package);
                request.Headers.TryAddWithoutValidation("gcm_ver", "201216023");
                request.Headers.TryAddWithoutValidation("User-Agent", "Android-GCM/1.5");
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                last = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var match = TokenPattern().Match(last);
                if (match.Success) return match.Groups[1].Value.Trim();
                _logger.LogDebug("[fcm] register3 attempt {Attempt}: {Response}", attempt + 1, Truncate(last, 120));
                await _delay(TimeSpan.FromMilliseconds(1000 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            throw new HttpRequestException($"GCM register failed: {Truncate(last, 200)}");
        }

        // JavaScript's '.' stops at a line terminator.
        [GeneratedRegex("token=([^\\n\\r\\u2028\\u2029]+)")]
        private static partial Regex TokenPattern();

        private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
    }
}
