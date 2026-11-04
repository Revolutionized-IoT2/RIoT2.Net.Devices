using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RIoT2.Core;

namespace RIoT2.Net.Devices.Services.Netatmo
{
    /// <summary>Netatmo refused a call or couldn't be reached. Devices keep their last state and try again later.</summary>
    public class NetatmoApiException : Exception
    {
        public NetatmoApiException(string message, HttpStatusCode? status = null, int? code = null, string? textCode = null, Exception? inner = null)
            : base(message, inner)
        {
            Status = status;
            Code = code;
            TextCode = textCode;
        }

        public HttpStatusCode? Status { get; }

        /// <summary>Netatmo's numeric error code, for example 26 (rate limit) or 13 (missing scope).</summary>
        public int? Code { get; }

        /// <summary>The text code of the webhooks API, for example <c>WH009</c> (one webhook per app).</summary>
        public string? TextCode { get; }

        /// <summary>Netatmo limits the calls: wait before the next one.</summary>
        public bool IsThrottled => Code == 26 || Code == 27 || Code == 11 || Status == HttpStatusCode.TooManyRequests;

        /// <summary>The tokens lack a scope the call needs: connect again.</summary>
        public bool IsMissingScope => Code == 13;

        /// <summary>Netatmo rejects the tokens and couldn't refresh them: connect again.</summary>
        public bool IsRejectedToken => Code == 2 || Code == 3 || Status == HttpStatusCode.Unauthorized;
    }

    /// <summary>The device has no Netatmo tokens yet: it must be connected first.</summary>
    public sealed class NetatmoNotConnectedException : NetatmoApiException
    {
        public NetatmoNotConnectedException() : base("Connect the device to Netatmo.") { }
    }

    /// <summary>
    /// Calls the Netatmo Connect API with the tokens of one app (<see cref="NetatmoAuthClient"/>): refreshes a rejected
    /// access token once and retries, and turns Netatmo's errors into <see cref="NetatmoApiException"/>.
    /// </summary>
    public sealed class NetatmoApiClient
    {
        public const string BaseUrl = "https://api.netatmo.com/";

        private readonly NetatmoAuthClient _auth;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        private readonly ILogger _logger;

        public NetatmoApiClient(NetatmoAuthClient auth, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, ILogger logger)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _logger = logger;
        }

        public NetatmoAuthClient Auth => _auth;

        public Task<T?> GetAsync<T>(string path, IEnumerable<KeyValuePair<string, string>>? query, CancellationToken cancellationToken) =>
            SendAsync<T>(HttpMethod.Get, path + Query(query), null, cancellationToken);

        /// <summary>A POST with form fields; a field may repeat, as <c>person_ids[]</c> does.</summary>
        public Task<T?> PostFormAsync<T>(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
        {
            var fields = form.ToList();
            return SendAsync<T>(HttpMethod.Post, path, () => new FormUrlEncodedContent(fields), cancellationToken);
        }

        public Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(body);
            return SendAsync<T>(HttpMethod.Post, path, () => new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
            SendAsync<object>(HttpMethod.Delete, path, null, cancellationToken);

        private static string Query(IEnumerable<KeyValuePair<string, string>>? query)
        {
            if (query == null)
                return "";
            var pairs = query.Where(pair => !string.IsNullOrEmpty(pair.Value))
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")
                .ToList();
            return pairs.Count == 0 ? "" : "?" + string.Join("&", pairs);
        }

        private async Task<T?> SendAsync<T>(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken cancellationToken)
        {
            var accessToken = _auth.AccessToken;
            if (string.IsNullOrEmpty(accessToken))
            {
                // Only a refresh token, as configured before the first refresh: get an access token first.
                if (!_auth.HasTokens || !await _auth.RefreshAsync(accessToken).ConfigureAwait(false))
                    throw new NetatmoNotConnectedException();
                accessToken = _auth.AccessToken;
            }

            var response = await SendOnceAsync(method, path, content, accessToken, cancellationToken).ConfigureAwait(false);
            if (await IsRejectedTokenAsync(response, cancellationToken).ConfigureAwait(false))
            {
                if (await _auth.RefreshAsync(accessToken).ConfigureAwait(false))
                {
                    response.Dispose();
                    response = await SendOnceAsync(method, path, content, _auth.AccessToken, cancellationToken).ConfigureAwait(false);
                }
                else
                    _logger.LogWarning("Could not refresh the Netatmo token of client {ClientId}.", _auth.ClientId);
            }

            using (response)
            {
                var body = response.Content == null ? "" : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return string.IsNullOrWhiteSpace(body) || typeof(T) == typeof(object) ? default : body.ToObj<T>();

                var error = ParseError(response.StatusCode, body, path);
                if (error.IsMissingScope)
                    _auth.ReportProblem("The Netatmo tokens lack a permission this device needs. Connect the device to Netatmo again.");
                else if (error.IsRejectedToken)
                    _auth.ReportProblem("Netatmo refused the stored token. Connect the device to Netatmo again.");
                throw error;
            }
        }

        private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, Func<HttpContent>? content, string accessToken, CancellationToken cancellationToken)
        {
            // A fresh request per send: the retry after a token refresh can't reuse one.
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (content != null)
                request.Content = content();
            try
            {
                return await _send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x) when (x is HttpRequestException || x is OperationCanceledException || x is TimeoutException)
            {
                // An HTTP timeout, not a cancellation: Netatmo is unavailable for now.
                throw new NetatmoApiException("Netatmo can't be reached.", inner: x);
            }
        }

        // 401, or 403 with an invalid (2) or expired (3) token code or none. A 403 for a rate limit (26) or a missing
        // scope (13) isn't solved by a new token.
        private static async Task<bool> IsRejectedTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return true;
            if (response.StatusCode != HttpStatusCode.Forbidden || response.Content == null)
                return false;
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            var code = ParseError(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), "").Code;
            return code == null || code == 2 || code == 3;
        }

        internal static NetatmoApiException ParseError(HttpStatusCode status, string body, string path)
        {
            int? code = null;
            string? textCode = null;
            string? message = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
                    if (error.TryGetProperty("code", out var c))
                    {
                        if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var number))
                            code = number;
                        else if (c.ValueKind == JsonValueKind.String)
                            textCode = c.GetString();
                    }
                    if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        message = m.GetString();
                }
            }
            catch (JsonException)
            {
                // Not JSON, for example a proxy's error page.
            }

            // Query values (ids) are left out of the message.
            var endpoint = path.Split('?')[0];
            var detail = message ?? status.ToString();
            var codeText = code?.ToString() ?? textCode;
            return new NetatmoApiException(
                $"Netatmo answered {(int)status} to {endpoint}: {detail}{(codeText == null ? "" : $" (code {codeText})")}.",
                status, code, textCode);
        }
    }
}
