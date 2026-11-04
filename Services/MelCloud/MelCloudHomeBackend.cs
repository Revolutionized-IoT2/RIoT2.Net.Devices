using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.MelCloud
{
    /// <summary>
    /// MELCloud Home (melcloudhome.com), the API of the MELCloud Home app. It is not documented; the requests follow
    /// the melcloudhome Home Assistant integration. The login is the app's OAuth 2.0 authorization code flow with
    /// PKCE, whose login form is AWS Cognito's; the refresh token changes on every refresh.
    /// </summary>
    internal sealed partial class MelCloudHomeBackend : IMelCloudBackend
    {
        internal const string ApiUrl = "https://mobile.bff.melcloudhome.com";
        internal const string AuthUrl = "https://auth.melcloudhome.com";
        private const string ClientId = "homemobile";
        private const string RedirectUri = "melcloudhome://";
        private const string Scopes = "openid profile email offline_access IdentityServerApi";
        private const string AppUserAgent = "MonitorAndControl.App.Mobile/52 CFNetwork/3860.400.51 Darwin/25.3.0";
        private const string BrowserUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/22F76";
        private const int MaxRedirects = 10;

        // MELCloud Home's names for the modes, fan speeds and vane positions, and the device's.
        private static readonly Dictionary<string, string> _modes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Heat"] = MelCloudModes.Heat, ["Cool"] = MelCloudModes.Cool, ["Automatic"] = MelCloudModes.Auto,
            ["Dry"] = MelCloudModes.Dry, ["Fan"] = MelCloudModes.Fan
        };
        private static readonly Dictionary<string, string> _speeds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto"] = "auto", ["One"] = "1", ["Two"] = "2", ["Three"] = "3", ["Four"] = "4", ["Five"] = "5"
        };
        private static readonly Dictionary<string, string> _verticalVanes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto"] = "auto", ["One"] = "1", ["Two"] = "2", ["Three"] = "3", ["Four"] = "4", ["Five"] = "5", ["Swing"] = "swing"
        };
        private static readonly Dictionary<string, string> _horizontalVanes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Auto"] = "auto", ["Left"] = "1", ["LeftCentre"] = "2", ["Centre"] = "3", ["RightCentre"] = "4", ["Right"] = "5", ["Swing"] = "swing"
        };
        // Some answers number the positions or spell them the American way.
        private static readonly Dictionary<string, string> _numberedSpeeds = new() { ["0"] = "Auto", ["1"] = "One", ["2"] = "Two", ["3"] = "Three", ["4"] = "Four", ["5"] = "Five" };
        private static readonly Dictionary<string, string> _numberedVanes = new() { ["0"] = "Auto", ["1"] = "One", ["2"] = "Two", ["3"] = "Three", ["4"] = "Four", ["5"] = "Five", ["7"] = "Swing" };
        private static readonly Dictionary<string, string> _americanVanes = new(StringComparer.OrdinalIgnoreCase) { ["CenterLeft"] = "LeftCentre", ["Center"] = "Centre", ["CenterRight"] = "RightCentre" };

        private readonly string _email;
        private readonly string _password;
        private readonly MelCloudSend _send;
        private readonly MelCloudSessionStore _store;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private MelCloudSession? _session;

        public MelCloudHomeBackend(string email, string password, MelCloudSend send, MelCloudSessionStore store, ILogger logger, TimeProvider time)
        {
            _email = email;
            _password = password;
            _send = send;
            _store = store;
            _logger = logger;
            _time = time;
        }

        public async Task<IReadOnlyList<MelCloudUnit>> GetUnitsAsync(CancellationToken cancellationToken)
        {
            var context = await SendJsonAsync(HttpMethod.Get, "/context", null, cancellationToken).ConfigureAwait(false);
            var units = new List<MelCloudUnit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var buildings = MelCloudJson.Objects(context?["buildings"]).Concat(MelCloudJson.Objects(context?["guestBuildings"]));
            foreach (var data in buildings.SelectMany(x => MelCloudJson.Objects(x["airToAirUnits"])))
            {
                var id = MelCloudJson.Text(data["id"]);
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    continue;

                var unit = ReadUnit(id, data);
                if (MelCloudJson.Bool(data["capabilities"]?["hasEnergyConsumedMeter"]) == true)
                {
                    try
                    {
                        unit.EnergyToday = await GetEnergyTodayAsync(id, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MelCloudUnavailableException error)
                    {
                        _logger.LogWarning("Could not read the energy of MELCloud Home unit {Unit}: {Reason}", id, error.Message);
                    }
                }
                units.Add(unit);
            }
            return units;
        }

        public async Task SetAsync(MelCloudUnit unit, MelCloudChange change, CancellationToken cancellationToken)
        {
            // The API wants every field; null leaves a setting as it is.
            var body = new JsonObject
            {
                ["power"] = change.Power,
                ["operationMode"] = change.Mode == null ? null : Key(_modes, change.Mode, "mode"),
                ["setFanSpeed"] = change.FanSpeed == null ? null : Key(_speeds, change.FanSpeed, "fan speed"),
                ["vaneHorizontalDirection"] = change.VaneHorizontal == null ? null : Key(_horizontalVanes, change.VaneHorizontal, "horizontal vane position"),
                ["vaneVerticalDirection"] = change.VaneVertical == null ? null : Key(_verticalVanes, change.VaneVertical, "vertical vane position"),
                ["setTemperature"] = change.SetTemperature,
                ["temperatureIncrementOverride"] = null,
                ["inStandbyMode"] = null
            };
            await SendJsonAsync(HttpMethod.Put, $"/monitor/ataunit/{Uri.EscapeDataString(unit.Id)}", body, cancellationToken).ConfigureAwait(false);
        }

        internal static MelCloudUnit ReadUnit(string id, JsonObject data)
        {
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in MelCloudJson.Objects(data["settings"]))
            {
                var name = MelCloudJson.Text(setting["name"]);
                if (name != null)
                    settings[name] = MelCloudJson.Text(setting["value"]);
            }
            var capabilities = data["capabilities"];

            var unit = new MelCloudUnit
            {
                Id = id,
                Name = MelCloudClassicBackend.Clean(MelCloudJson.Text(data["givenDisplayName"])) ?? id,
                Power = bool.TryParse(Setting(settings, "Power"), out var power) ? power : null,
                Mode = Name(_modes, Setting(settings, "OperationMode")),
                SetTemperature = Number(Setting(settings, "SetTemperature")),
                RoomTemperature = Number(Setting(settings, "RoomTemperature")),
                FanSpeed = Name(_speeds, Renumber(_numberedSpeeds, Setting(settings, "SetFanSpeed"))),
                VaneVertical = Name(_verticalVanes, Renumber(_numberedVanes, Setting(settings, "VaneVerticalDirection"))),
                VaneHorizontal = Name(_horizontalVanes, Renumber(_americanVanes, Setting(settings, "VaneHorizontalDirection"))),
                Offline = MelCloudJson.Bool(data["isConnected"]) is bool connected ? !connected : null,
                TemperatureStep = MelCloudJson.Bool(capabilities?["hasHalfDegreeIncrements"]) == false ? 1 : 0.5
            };
            var (min, max, minDefault) = unit.Mode switch
            {
                MelCloudModes.Cool or MelCloudModes.Dry => ("minTempCoolDry", "maxTempCoolDry", 16d),
                MelCloudModes.Auto => ("minTempAutomatic", "maxTempAutomatic", 16d),
                _ => ("minTempHeat", "maxTempHeat", 10d)
            };
            unit.MinTemperature = MelCloudJson.Number(capabilities?[min]) ?? minDefault;
            unit.MaxTemperature = MelCloudJson.Number(capabilities?[max]) ?? 31;
            return unit;
        }

        /// <summary>The unit's consumption since local midnight in kWh: the sum of the hourly readings (Wh).</summary>
        private async Task<double?> GetEnergyTodayAsync(string id, CancellationToken cancellationToken)
        {
            var now = _time.GetLocalNow();
            var midnight = new DateTimeOffset(now.Date, now.Offset).ToUniversalTime();
            var query = string.Join("&",
                $"from={Uri.EscapeDataString(midnight.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}",
                $"to={Uri.EscapeDataString(now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}",
                "interval=Hour",
                "measure=cumulative_energy_consumed_since_last_upload");
            var answer = await SendJsonAsync(HttpMethod.Get, $"/telemetry/telemetry/energy/{Uri.EscapeDataString(id)}?{query}", null, cancellationToken).ConfigureAwait(false);
            // 304 (nothing new) has no body.
            if (answer == null)
                return null;

            var values = MelCloudJson.Objects(answer["measureData"]).FirstOrDefault()?["values"];
            var readings = MelCloudJson.Objects(values).Select(x => MelCloudJson.Number(x["value"])).Where(x => x != null).ToList();
            return readings.Count == 0 ? 0 : Math.Round(readings.Sum(x => x!.Value) / 1000, 3);
        }

        private async Task<JsonNode?> SendJsonAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                var token = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, ApiUrl + path);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", AppUserAgent);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                if (body != null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var response = await MelCloudHttp.SendAsync(_send, request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (attempt > 0)
                        throw new MelCloudAuthenticationException($"MELCloud Home refused the login for {path}.");
                    // The access token was revoked before it expired: refresh it, or log in again, once.
                    _session!.AccessToken = null;
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return null;
                await MelCloudHttp.EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
                return await MelCloudHttp.ReadJsonAsync(response, path, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
        {
            _session ??= _store.Load() ?? new MelCloudSession();
            if (!string.IsNullOrEmpty(_session.AccessToken) && _session.ExpiresAt > _time.GetUtcNow().AddMinutes(1))
                return _session.AccessToken;

            if (!string.IsNullOrEmpty(_session.RefreshToken))
            {
                try
                {
                    await TokenAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = _session.RefreshToken,
                        ["client_id"] = ClientId
                    }, cancellationToken).ConfigureAwait(false);
                    return _session.AccessToken!;
                }
                catch (MelCloudAuthenticationException)
                {
                    _logger.LogInformation("MELCloud Home refused the refresh token of {Email}; logging in again.", MelCloudHttp.Mask(_email));
                }
            }

            await LoginAsync(cancellationToken).ConfigureAwait(false);
            return _session.AccessToken!;
        }

        /// <summary>
        /// The app's login: a pushed authorization request, the authorize redirects to Cognito's login form, the
        /// credentials, the callback to <c>melcloudhome://</c> with the code, and the code for the tokens. When the
        /// identity server still has a session, the authorize redirects go to the callback without the form.
        /// </summary>
        private async Task LoginAsync(CancellationToken cancellationToken)
        {
            var cookies = new CookieContainer();
            var verifier = RandomText(32);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            string requestUri;
            using (var request = Form(HttpMethod.Post, $"{AuthUrl}/connect/par", new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["state"] = RandomText(16),
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["client_id"] = ClientId,
                ["scope"] = Scopes,
                ["redirect_uri"] = RedirectUri
            }, AppUserAgent))
            using (var response = await MelCloudHttp.SendAsync(_send, request, cancellationToken).ConfigureAwait(false))
            {
                await MelCloudHttp.EnsureSuccessAsync(response, "the login request", cancellationToken).ConfigureAwait(false);
                requestUri = MelCloudJson.Text((await MelCloudHttp.ReadJsonAsync(response, "the login request", cancellationToken).ConfigureAwait(false))?["request_uri"])
                    ?? throw new MelCloudUnavailableException("MELCloud Home's login request answer has no request_uri.");
            }

            var page = await FollowAsync(HttpMethod.Get, new Uri($"{AuthUrl}/connect/authorize?client_id={ClientId}&request_uri={Uri.EscapeDataString(requestUri)}"),
                null, cookies, AppUserAgent, cancellationToken).ConfigureAwait(false);
            var code = page.Code;
            string? callback = null;
            if (code == null && IsCognito(page.Url) && page.Url.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                var csrf = Csrf(page.Body) ?? throw new MelCloudUnavailableException("MELCloud Home's login form has no CSRF token.");
                var posted = await FollowAsync(HttpMethod.Post, page.Url, new Dictionary<string, string>
                {
                    ["_csrf"] = csrf,
                    ["username"] = _email,
                    ["password"] = _password,
                    ["cognitoAsfData"] = ""
                }, cookies, BrowserUserAgent, cancellationToken).ConfigureAwait(false);
                code = posted.Code;
                if (code == null && IsCognito(posted.Url))
                {
                    _store.Delete();
                    throw new MelCloudAuthenticationException($"MELCloud Home refused the login of {MelCloudHttp.Mask(_email)}; check the e-mail and password.");
                }
                page = posted;
            }
            if (code == null)
            {
                callback = Callback(page.Body);
                code = callback == null ? Code(page.Url.OriginalString) ?? Code(page.Body) : null;
            }
            if (code == null && callback != null)
                code = (await FollowAsync(HttpMethod.Get, new Uri($"{AuthUrl}/connect/authorize/callback?{WebUtility.HtmlDecode(callback)}"),
                    null, cookies, AppUserAgent, cancellationToken).ConfigureAwait(false)).Code;
            if (code == null)
                throw new MelCloudUnavailableException($"MELCloud Home's login ended at {page.Url.GetLeftPart(UriPartial.Path)} without a code.");

            await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = verifier,
                ["client_id"] = ClientId
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Logged in to MELCloud Home as {Email}.", MelCloudHttp.Mask(_email));
        }

        private async Task TokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            using var request = Form(HttpMethod.Post, $"{AuthUrl}/connect/token", form, AppUserAgent);
            using var response = await MelCloudHttp.SendAsync(_send, request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                if (form["grant_type"] != "refresh_token")
                    throw new MelCloudUnavailableException($"MELCloud Home refused the login code ({(int)response.StatusCode}).");
                // The refresh token expired or another client rotated it; the caller logs in again.
                _session = new MelCloudSession();
                _store.Delete();
                throw new MelCloudAuthenticationException("MELCloud Home refused the refresh token.");
            }
            await MelCloudHttp.EnsureSuccessAsync(response, "the token request", cancellationToken).ConfigureAwait(false);
            var answer = await MelCloudHttp.ReadJsonAsync(response, "the token request", cancellationToken).ConfigureAwait(false);
            var accessToken = MelCloudJson.Text(answer?["access_token"])
                ?? throw new MelCloudUnavailableException("MELCloud Home's token answer has no access token.");

            _session = new MelCloudSession
            {
                AccessToken = accessToken,
                // A refresh may not rotate the refresh token: keep the one sent.
                RefreshToken = MelCloudJson.Text(answer?["refresh_token"]) ?? form.GetValueOrDefault("refresh_token"),
                ExpiresAt = _time.GetUtcNow().AddSeconds(MelCloudJson.Number(answer?["expires_in"]) ?? 3600)
            };
            _store.Save(_session);
        }

        private sealed record Page(Uri Url, string Body, string? Code);

        /// <summary>
        /// Sends a request and follows its redirects, with the login's own cookies. A redirect to the app
        /// (<c>melcloudhome://</c>) ends it with the code it carries.
        /// </summary>
        private async Task<Page> FollowAsync(HttpMethod method, Uri url, Dictionary<string, string>? form, CookieContainer cookies, string userAgent, CancellationToken cancellationToken)
        {
            var origin = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = form != null ? Form(method, url.AbsoluteUri, form, userAgent) : new HttpRequestMessage(method, url);
                if (form != null)
                {
                    request.Headers.TryAddWithoutValidation("Origin", origin.GetLeftPart(UriPartial.Authority));
                    request.Headers.TryAddWithoutValidation("Referer", origin.AbsoluteUri);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                }
                var cookie = cookies.GetCookieHeader(url);
                if (cookie.Length > 0)
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);

                using var response = await MelCloudHttp.SendAsync(_send, request, cancellationToken).ConfigureAwait(false);
                if (response.Headers.NonValidated.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var value in setCookies)
                    {
                        try
                        {
                            cookies.SetCookies(url, value);
                        }
                        catch (CookieException)
                        {
                            // A cookie the container can't store isn't one the login needs.
                        }
                    }
                }

                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.NonValidated.TryGetValues("Location", out var locations))
                {
                    var location = locations.ToString().Trim();
                    if (location.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase) || location.StartsWith("melcloudhome:", StringComparison.OrdinalIgnoreCase))
                        return new Page(url, "", Code(location) ?? throw new MelCloudUnavailableException("MELCloud Home redirected to the app without a code."));

                    url = new Uri(url, location);
                    // 307 and 308 repeat the request; the others continue with a GET.
                    if (status is not (307 or 308))
                    {
                        method = HttpMethod.Get;
                        form = null;
                    }
                    continue;
                }

                if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                    await MelCloudHttp.EnsureSuccessAsync(response, "the login", cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new Page(url, body, null);
            }
            throw new MelCloudUnavailableException("MELCloud Home's login redirected too often.");
        }

        private static HttpRequestMessage Form(HttpMethod method, string url, Dictionary<string, string> form, string userAgent)
        {
            var request = new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(form) };
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        private static bool IsCognito(Uri url) => url.Host.EndsWith(".amazoncognito.com", StringComparison.OrdinalIgnoreCase);

        internal static string? Csrf(string html)
        {
            var match = CsrfNameFirst().Match(html);
            if (!match.Success)
                match = CsrfValueFirst().Match(html);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        internal static string? Code(string text)
        {
            var match = CodePattern().Match(text);
            return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
        }

        private static string? Callback(string body)
        {
            var match = CallbackPattern().Match(body);
            return match.Success ? match.Groups[1].Value : null;
        }

        [GeneratedRegex("""<input[^>]+name="_csrf"[^>]+value="([^"]+)"[^>]*>""", RegexOptions.IgnoreCase)]
        private static partial Regex CsrfNameFirst();

        [GeneratedRegex("""<input[^>]+value="([^"]+)"[^>]+name="_csrf"[^>]*>""", RegexOptions.IgnoreCase)]
        private static partial Regex CsrfValueFirst();

        // "code=" after "?", "&" or "&amp;"; not code_challenge.
        [GeneratedRegex("""(?:[?&;#])code=([^&"'\s<>]+)""")]
        private static partial Regex CodePattern();

        [GeneratedRegex("""/connect/authorize/callback\?([^"'\s<>]+)""")]
        private static partial Regex CallbackPattern();

        private static string RandomText(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

        private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string? Setting(Dictionary<string, string?> settings, string name) => settings.GetValueOrDefault(name);

        private static double? Number(string? text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

        private static string? Renumber(Dictionary<string, string> names, string? value) =>
            value != null && names.TryGetValue(value, out var name) ? name : value;

        private static string? Name(Dictionary<string, string> names, string? value) =>
            value != null && names.TryGetValue(value, out var name) ? name : null;

        private static string Key(Dictionary<string, string> names, string name, string what)
        {
            foreach (var (key, value) in names)
            {
                if (value == name)
                    return key;
            }
            throw new ArgumentException($"MELCloud Home has no {what} '{name}'.");
        }
    }
}
