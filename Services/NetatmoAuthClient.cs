using System.Net;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services
{
    /// <summary>
    /// Access and refresh tokens of one Netatmo app (client id). Netatmo rotates the refresh token on every refresh,
    /// so the latest tokens are persisted and win over the ones in the device configuration. Devices that share a
    /// client id share one instance, and only one of them refreshes at a time.
    /// </summary>
    public sealed class NetatmoAuthClient
    {
        internal const string TokenUrl = "https://api.netatmo.com/oauth2/token";

        private readonly object _lock = new();
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private readonly ILogger _logger;
        private readonly Func<string, string, Task<HttpResponseMessage>> _postForm;
        private string _accessToken = "";
        private string _refreshToken = "";
        private string _clientSecret = "";
        private string _configuredAccessToken;
        private string _configuredRefreshToken;
        private bool _loaded;

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

        /// <summary>
        /// Applies the tokens and secret of a device configuration. The first call loads the stored tokens, which
        /// win over the configured ones; without a stored file the configured tokens are used.
        /// </summary>
        public void Configure(string accessToken, string refreshToken, string clientSecret)
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
                        }
                        return true;
                    }
                }

                _logger.LogWarning("Netatmo refused to refresh the token of client {ClientId}. Using the configured tokens again. Message: {Message}", ClientId, content);
                Delete();
                lock (_lock)
                {
                    _accessToken = _configuredAccessToken;
                    _refreshToken = _configuredRefreshToken;
                    return _accessToken != rejectedAccessToken;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private NetatmoAuth Load()
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
