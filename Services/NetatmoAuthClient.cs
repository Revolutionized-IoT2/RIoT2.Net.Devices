using System.Net;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services
{
    /// <summary>
    /// Access and refresh tokens of one Netatmo app (client id). The tokens come from connecting a device in the UI
    /// (<see cref="ExchangeCodeAsync"/>), or from the device configuration. Netatmo rotates the refresh token on every
    /// refresh, so the latest tokens are persisted and win over the ones in the device configuration. Devices that
    /// share a client id share one instance, and only one of them refreshes at a time.
    /// </summary>
    public sealed class NetatmoAuthClient
    {
        internal const string TokenUrl = "https://api.netatmo.com/oauth2/token";
        internal const string AuthorizeUrl = "https://api.netatmo.com/oauth2/authorize";

        private readonly object _lock = new();
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private readonly ILogger _logger;
        private readonly Func<string, string, Task<HttpResponseMessage>> _postForm;
        private readonly SortedSet<string> _scopes = new(StringComparer.Ordinal);
        private string _accessToken = "";
        private string _refreshToken = "";
        private string _clientSecret = "";
        // Configure sets these; NetatmoBase only uses a client after configuring it.
        private string _configuredAccessToken = null!;
        private string _configuredRefreshToken = null!;
        private bool _loaded;
        private string? _problem;
        private string? _account;

        internal NetatmoAuthClient(string clientId, string tokenFile, ILogger logger, Func<string, string, Task<HttpResponseMessage>> postForm)
        {
            ClientId = clientId;
            TokenFile = tokenFile;
            _logger = logger;
            _postForm = postForm;
        }

        public string ClientId { get; }

        /// <summary>Where the rotated tokens are stored.</summary>
        public string TokenFile { get; }

        public string AccessToken
        {
            get { lock (_lock) return _accessToken; }
        }

        internal string RefreshToken
        {
            get { lock (_lock) return _refreshToken; }
        }

        /// <summary>Whether there are tokens to call Netatmo with: an access token, or a refresh token to get one.</summary>
        public bool HasTokens
        {
            get { lock (_lock) return _accessToken.Length > 0 || _refreshToken.Length > 0; }
        }

        /// <summary>Whether the client secret is known, which the sign-in and every refresh need.</summary>
        public bool HasClientSecret
        {
            get { lock (_lock) return _clientSecret.Length > 0; }
        }

        /// <summary>Why the stored credentials stopped working (refused, or missing a scope); null while they work.</summary>
        public string? Problem
        {
            get { lock (_lock) return _problem; }
        }

        /// <summary>The account the tokens belong to (its e-mail address), when a device has read it.</summary>
        public string? Account
        {
            get { lock (_lock) return _account; }
            set { lock (_lock) _account = string.IsNullOrWhiteSpace(value) ? _account : value; }
        }

        /// <summary>
        /// The scopes the devices of this app need. Devices sharing a client id share its tokens, so a sign-in asks for
        /// all of them: connecting one device must not take away what another one needs.
        /// </summary>
        public IReadOnlyCollection<string> Scopes
        {
            get { lock (_lock) return _scopes.ToArray(); }
        }

        /// <summary>Adds scopes the next sign-in asks for.</summary>
        public void RequireScopes(IEnumerable<string> scopes)
        {
            lock (_lock)
            {
                foreach (var scope in scopes)
                    if (!string.IsNullOrWhiteSpace(scope))
                        _scopes.Add(scope.Trim());
            }
        }

        /// <summary>Records why Netatmo refused a call with these tokens, for the device's authorization status.</summary>
        public void ReportProblem(string problem)
        {
            lock (_lock) _problem = problem;
        }

        /// <summary>Netatmo's sign-in page for this app, asking for <see cref="Scopes"/>.</summary>
        public string BuildAuthorizeUrl(string redirectUri, string state)
        {
            var scope = string.Join(" ", Scopes);
            return $"{AuthorizeUrl}?client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&scope={Uri.EscapeDataString(scope)}&state={Uri.EscapeDataString(state)}&response_type=code";
        }

        /// <summary>
        /// Exchanges the code Netatmo's sign-in returned for tokens, stores them like refreshed ones and uses them at once.
        /// Throws <see cref="InvalidOperationException"/> with Netatmo's reason when it refuses the code.
        /// </summary>
        public async Task ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string clientSecret;
                string scope;
                lock (_lock)
                {
                    clientSecret = _clientSecret;
                    scope = string.Join(" ", _scopes);
                }

                var body = $"grant_type=authorization_code&client_id={Uri.EscapeDataString(ClientId)}" +
                    $"&client_secret={Uri.EscapeDataString(clientSecret)}&code={Uri.EscapeDataString(code)}" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}";

                HttpResponseMessage? response;
                try
                {
                    response = await _postForm(TokenUrl, body).ConfigureAwait(false);
                }
                catch (Exception x) when (x is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Could not reach Netatmo to finish the sign-in.", x);
                }

                using (response)
                {
                    var content = response?.Content == null ? "" : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var auth = response?.StatusCode == HttpStatusCode.OK ? content.ToObj<NetatmoAuth>() : null;
                    if (string.IsNullOrEmpty(auth?.access_token))
                    {
                        // Netatmo answers {"error":"invalid_grant"} and the like; no secrets in it.
                        _logger.LogWarning("Netatmo refused the sign-in of client {ClientId}. Status: {Status}", ClientId, response?.StatusCode);
                        throw new InvalidOperationException($"Netatmo refused the sign-in ({DescribeError(content, response?.StatusCode)}).");
                    }

                    auth.client_id = ClientId;
                    Save(auth);
                    lock (_lock)
                    {
                        _accessToken = auth.access_token;
                        _refreshToken = auth.refresh_token ?? "";
                        _problem = null;
                        _loaded = true;
                    }
                    _logger.LogInformation("Netatmo client {ClientId} is connected.", ClientId);
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        // OAuth's answer to a refresh token or client it won't accept: 400 or 401 with invalid_grant or invalid_client.
        internal static bool IsRefusedGrant(HttpStatusCode status, string content)
        {
            if (status != HttpStatusCode.BadRequest && status != HttpStatusCode.Unauthorized)
                return false;
            var error = DescribeError(content, status);
            return error is "invalid_grant" or "invalid_client" or "unauthorized_client";
        }

        // Netatmo's OAuth errors are {"error":"invalid_grant"}; its API errors {"error":{"code":..,"message":..}}.
        internal static string DescribeError(string content, HttpStatusCode? status)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == System.Text.Json.JsonValueKind.String)
                        return error.GetString()!;
                    if (error.ValueKind == System.Text.Json.JsonValueKind.Object && error.TryGetProperty("message", out var message)
                        && message.ValueKind == System.Text.Json.JsonValueKind.String)
                        return message.GetString()!;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON: fall back to the status.
            }
            return $"HTTP {(int?)status}";
        }

        /// <summary>
        /// Applies the tokens and secret of a device configuration. The first call loads the stored tokens, which
        /// win over the configured ones; without a stored file the configured tokens are used.
        /// </summary>
        public void Configure(string? accessToken, string? refreshToken, string? clientSecret)
        {
            lock (_lock)
            {
                _clientSecret = clientSecret ?? "";
                _configuredAccessToken = accessToken ?? "";
                _configuredRefreshToken = refreshToken ?? "";
                if (_loaded)
                    return;

                var stored = Load();
                _accessToken = stored?.access_token ?? _configuredAccessToken;
                _refreshToken = stored?.refresh_token ?? _configuredRefreshToken;
                _loaded = true;
            }
        }

        /// <summary>
        /// Gets a new access token after Netatmo rejected <paramref name="rejectedAccessToken"/>. Returns true when a
        /// token other than the rejected one is available, including one another device refreshed meanwhile.
        /// When Netatmo refuses the refresh token, the stored file is deleted and the configured tokens are used again.
        /// </summary>
        public async Task<bool> RefreshAsync(string rejectedAccessToken)
        {
            await _refreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string refreshToken;
                string clientSecret;
                lock (_lock)
                {
                    if (_accessToken != rejectedAccessToken)
                        return true;
                    refreshToken = _refreshToken;
                    clientSecret = _clientSecret;
                }

                // Nothing to refresh with: the device isn't connected yet.
                if (string.IsNullOrEmpty(refreshToken))
                    return false;

                var body = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}" +
                    $"&client_id={Uri.EscapeDataString(ClientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";

                HttpResponseMessage response;
                try
                {
                    response = await _postForm(TokenUrl, body).ConfigureAwait(false);
                }
                catch (Exception x)
                {
                    // Network trouble: keep the stored tokens, they may still be valid.
                    _logger.LogWarning(x, "Could not reach Netatmo to refresh the token of client {ClientId}.", ClientId);
                    return false;
                }

                if (response == null)
                {
                    _logger.LogWarning("Could not reach Netatmo to refresh the token of client {ClientId}.", ClientId);
                    return false;
                }

                var content = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var auth = content.ToObj<NetatmoAuth>();
                    if (!string.IsNullOrEmpty(auth?.access_token))
                    {
                        auth.client_id = ClientId;
                        Save(auth);
                        lock (_lock)
                        {
                            _accessToken = auth.access_token;
                            _refreshToken = auth.refresh_token ?? refreshToken;
                            _problem = null;
                        }
                        return true;
                    }
                }

                // Only a refused grant ends the stored tokens. A rate limit, an outage or a proxy's page keeps them:
                // after a sign-in from the UI they are the only copy.
                if (!IsRefusedGrant(response.StatusCode, content))
                {
                    _logger.LogWarning("Could not refresh the Netatmo token of client {ClientId}: HTTP {Status}. Keeping the stored tokens.", ClientId, (int)response.StatusCode);
                    return false;
                }

                _logger.LogWarning("Netatmo refused to refresh the token of client {ClientId}. Using the configured tokens again. Message: {Message}", ClientId, content);
                Delete();
                lock (_lock)
                {
                    _accessToken = _configuredAccessToken;
                    _refreshToken = _configuredRefreshToken;
                    var fallback = _accessToken != rejectedAccessToken;
                    if (!fallback)
                        _problem = "Netatmo refused the stored token. Connect the device to Netatmo again.";
                    return fallback;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private NetatmoAuth? Load()
        {
            try
            {
                if (!File.Exists(TokenFile))
                    return null;
                return File.ReadAllText(TokenFile).ToObj<NetatmoAuth>();
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not load Netatmo authentication file {File}", TokenFile);
                throw new InvalidOperationException($"Could not load Netatmo authentication file {TokenFile}", x);
            }
        }

        private void Save(NetatmoAuth auth)
        {
            try
            {
                var directory = Path.GetDirectoryName(TokenFile);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // Write a temporary file first, so a crash can't leave a half-written token file behind.
                var temporary = TokenFile + ".tmp";
                File.WriteAllText(temporary, auth.ToJson());
                File.Move(temporary, TokenFile, overwrite: true);
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not save Netatmo authentication file {File}", TokenFile);
                throw new InvalidOperationException($"Could not save Netatmo authentication file {TokenFile}", x);
            }
        }

        private void Delete()
        {
            try
            {
                File.Delete(TokenFile);
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not delete Netatmo authentication file {File}", TokenFile);
            }
        }
    }
}
