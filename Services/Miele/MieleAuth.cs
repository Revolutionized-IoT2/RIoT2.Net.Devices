using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Miele
{
    /// <summary>The settings of a Miele login: the app registered at developer.miele.com and the ways to sign in.</summary>
    internal sealed record MieleAuthSettings(string ClientId, string ClientSecret, string? Email, string? Password, string Country,
        string? AuthorizationCode, string RedirectUri);

    /// <summary>
    /// The OAuth tokens of a Miele app. It uses the stored tokens and refreshes them; without them it logs in with the
    /// e-mail and password through Miele's login form, or exchanges a pasted authorization code (each code once).
    /// The tokens are kept in a file, because a refresh token lives long and a login needs the user. One login at a
    /// time, so the event stream and a poll don't log in twice.
    /// </summary>
    internal sealed partial class MieleAuth
    {
        internal const string AuthUrl = "https://api.mcs3.miele.com";
        internal const string TokenUrl = AuthUrl + "/thirdparty/token";
        internal const string HeadlessUrl = AuthUrl + "/oauth/auth";
        internal const string BrowserUrl = AuthUrl + "/thirdparty/login";

        /// <summary>A token that expires sooner than this is refreshed before it is used.</summary>
        internal static readonly TimeSpan RefreshMargin = TimeSpan.FromDays(1);

        private readonly MieleAuthSettings _settings;
        private readonly MieleSend _send;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private MieleTokens? _tokens;

        public MieleAuth(MieleAuthSettings settings, MieleSend send, string tokenFile, ILogger logger, TimeProvider time)
        {
            _settings = settings;
            _send = send;
            TokenFile = tokenFile;
            _logger = logger;
            _time = time;
        }

        public string TokenFile { get; }

        /// <summary>The page where the user logs in to get an authorization code.</summary>
        public string LoginUrl =>
            $"{BrowserUrl}?client_id={Uri.EscapeDataString(_settings.ClientId)}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}&state=riot2&vg={Uri.EscapeDataString(_settings.Country)}";

        /// <summary>
        /// A valid access token. <paramref name="rejected"/> is a token Miele refused (401): then a new one is fetched,
        /// unless another caller already did.
        /// </summary>
        public async Task<string> GetTokenAsync(string? rejected, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var tokens = _tokens ??= Load() ?? new MieleTokens();
                var now = _time.GetUtcNow();
                if (tokens.AccessToken != null && (rejected == null ? tokens.ExpiresAt - now > RefreshMargin : tokens.AccessToken != rejected))
                    return tokens.AccessToken;

                if (tokens.RefreshToken != null)
                {
                    try
                    {
                        return Store(await RequestTokenAsync(
                            $"grant_type=refresh_token&refresh_token={Escape(tokens.RefreshToken)}&client_id={Escape(_settings.ClientId)}" +
                            $"&client_secret={Escape(_settings.ClientSecret)}", "the token refresh", cancellationToken).ConfigureAwait(false));
                    }
                    catch (MieleRefusedException error)
                    {
                        _logger.LogWarning("Miele refused the refresh token; logging in again. {Reason}", error.Message);
                        tokens.AccessToken = null;
                        tokens.RefreshToken = null;
                        Save(tokens);
                    }
                    catch (MieleUnavailableException) when (rejected == null && tokens.AccessToken != null && tokens.ExpiresAt > now)
                    {
                        // The token still works for a while; refresh it next time.
                        return tokens.AccessToken;
                    }
                }

                string? loginProblem = null;
                if (!string.IsNullOrWhiteSpace(_settings.Email) && !string.IsNullOrEmpty(_settings.Password))
                {
                    try
                    {
                        var code = await HeadlessLoginAsync(cancellationToken).ConfigureAwait(false);
                        return Store(await ExchangeAsync(code, "/v1/devices", cancellationToken).ConfigureAwait(false));
                    }
                    catch (MieleRefusedException error)
                    {
                        loginProblem = error.Message;
                        _logger.LogWarning("Miele's login with the e-mail and password failed: {Reason}", error.Message);
                    }
                }

                var pasted = Code(_settings.AuthorizationCode);
                if (pasted != null && pasted != tokens.UsedCode)
                {
                    // A code works once: remember it before using it, so a refused one isn't sent again.
                    tokens.UsedCode = pasted;
                    Save(tokens);
                    try
                    {
                        return Store(await ExchangeAsync(pasted, _settings.RedirectUri, cancellationToken).ConfigureAwait(false));
                    }
                    catch (MieleRefusedException error)
                    {
                        throw new MieleAuthenticationException(
                            $"Miele refused the authorization code ({error.Message}). Log in at {LoginUrl} and paste the new code into authorizationCode.");
                    }
                }

                throw new MieleAuthenticationException(loginProblem != null
                    ? $"Miele refused the login of {Mask(_settings.Email!)}; check the e-mail, password and country. Or log in at {LoginUrl} and paste the code into authorizationCode."
                    : $"Log in to Miele at {LoginUrl} and paste the code from the address you are sent to into authorizationCode (or set the e-mail and password).");
            }
            finally
            {
                _gate.Release();
            }
        }

        // Miele's login form answers a good login with a redirect whose address has the code.
        private async Task<string> HeadlessLoginAsync(CancellationToken cancellationToken)
        {
            var form = $"email={Escape(_settings.Email!.Trim())}&password={Escape(_settings.Password!)}&redirect_uri={Escape("/v1/")}" +
                $"&state=login&response_type=code&client_id={Escape(_settings.ClientId)}&vgInformationSelector={Escape(_settings.Country)}";
            using var request = Form(HeadlessUrl, form);
            using var response = await MieleClient.SendAsync(_send, request, HttpCompletionOption.ResponseContentRead, "the login", cancellationToken)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && Code(response.Headers.Location?.OriginalString) is string code)
                return code;
            if (status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MieleUnavailableException($"Miele answered {status} {response.ReasonPhrase} for the login.");
            throw new MieleRefusedException(response.StatusCode, "Miele didn't accept the e-mail and password.");
        }

        private Task<MieleTokens> ExchangeAsync(string code, string redirectUri, CancellationToken cancellationToken) =>
            RequestTokenAsync(
                $"grant_type=authorization_code&code={Escape(code)}&redirect_uri={Escape(redirectUri)}&client_id={Escape(_settings.ClientId)}" +
                $"&client_secret={Escape(_settings.ClientSecret)}&vg={Escape(_settings.Country)}&state=token", "the authorization code", cancellationToken);

        private async Task<MieleTokens> RequestTokenAsync(string form, string what, CancellationToken cancellationToken)
        {
            using var request = Form(TokenUrl, form);
            using var response = await MieleClient.SendAsync(_send, request, HttpCompletionOption.ResponseContentRead, what, cancellationToken)
                .ConfigureAwait(false);
            await MieleClient.EnsureSuccessAsync(response, what, cancellationToken).ConfigureAwait(false);
            var json = await MieleClient.ReadJsonAsync(response, what, cancellationToken).ConfigureAwait(false);
            var access = MieleAppliance.Text(json?["access_token"]);
            if (string.IsNullOrEmpty(access))
                throw new MieleRefusedException(response.StatusCode, $"Miele's answer for {what} has no access token.");
            var lifetime = MieleAppliance.Number(json?["expires_in"]) is double seconds && seconds > 0 ? seconds : 3600;
            return new MieleTokens
            {
                AccessToken = access,
                RefreshToken = MieleAppliance.Text(json?["refresh_token"]) ?? _tokens?.RefreshToken,
                ExpiresAt = _time.GetUtcNow().AddSeconds(lifetime)
            };
        }

        private string Store(MieleTokens tokens)
        {
            tokens.UsedCode = _tokens?.UsedCode;
            _tokens = tokens;
            Save(tokens);
            _logger.LogInformation("Miele login refreshed; the token is valid until {Expires:u}.", tokens.ExpiresAt);
            return tokens.AccessToken!;
        }

        private static HttpRequestMessage Form(string url, string form) => new(HttpMethod.Post, url)
        {
            Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded")
        };

        private static string Escape(string text) => Uri.EscapeDataString(text);

        /// <summary>The code in a pasted text: the code itself, or the address the login sent the browser to.</summary>
        internal static string? Code(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var match = CodePattern().Match(text);
            var code = match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : text.Trim();
            return code.Length == 0 || code.Contains(' ', StringComparison.Ordinal) ? null : code;
        }

        [GeneratedRegex(@"[?&#]code=([^&#\s]+)")]
        private static partial Regex CodePattern();

        internal static string Mask(string email)
        {
            var at = email.IndexOf('@');
            return at <= 0 ? "***" : $"{email[0]}***@{(at + 1 < email.Length ? email[at + 1] : '*')}***";
        }

        private MieleTokens? Load()
        {
            try
            {
                return File.Exists(TokenFile) ? JsonSerializer.Deserialize<MieleTokens>(File.ReadAllText(TokenFile)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(error, "Could not read the Miele token file {File}; logging in again.", TokenFile);
                return null;
            }
        }

        private void Save(MieleTokens tokens)
        {
            try
            {
                var directory = Path.GetDirectoryName(TokenFile);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = TokenFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(tokens));
                File.Move(temporary, TokenFile, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "Could not save the Miele token file {File}; the next start logs in again.", TokenFile);
            }
        }
    }
}
