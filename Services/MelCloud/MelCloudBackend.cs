using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Security;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.MelCloud
{
    /// <summary>Sends one HTTP request. The device's <c>SendAsync</c>, which the tests replace.</summary>
    internal delegate Task<HttpResponseMessage> MelCloudSend(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>One MELCloud service: classic MELCloud or MELCloud Home. It logs in when it needs to.</summary>
    internal interface IMelCloudBackend
    {
        /// <summary>The account's air-to-air units with their current state.</summary>
        Task<IReadOnlyList<MelCloudUnit>> GetUnitsAsync(CancellationToken cancellationToken);

        /// <summary>Sends <paramref name="change"/> to <paramref name="unit"/>.</summary>
        Task SetAsync(MelCloudUnit unit, MelCloudChange change, CancellationToken cancellationToken);
    }

    /// <summary>MELCloud refused the e-mail or the password; retrying doesn't help.</summary>
    internal sealed class MelCloudAuthenticationException(string message) : Exception(message);

    /// <summary>MELCloud didn't answer, limited the requests or answered something unexpected; a later poll may succeed.</summary>
    internal sealed class MelCloudUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>
    /// The HTTP client of the MELCloud backends. It follows no redirects and keeps no cookies, because the MELCloud
    /// Home login handles both itself, and it checks every request against the node's outbound URL policy.
    /// </summary>
    internal static class MelCloudHttp
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private static readonly HttpClient _client = new(new PolicyHandler(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        }))
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                return await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"MELCloud did not answer in {Timeout.TotalSeconds:0} seconds.", error);
            }
        }

        private sealed class PolicyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                OutboundUrlPolicyHandler.Check(Core.Utils.Web.OutboundUrlPolicy, request.RequestUri, OutboundPurposes.Device);
                return base.SendAsync(request, cancellationToken);
            }
        }

        /// <summary>Sends <paramref name="request"/>; a failure to connect or a timeout is <see cref="MelCloudUnavailableException"/>.</summary>
        internal static async Task<HttpResponseMessage> SendAsync(MelCloudSend send, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await send(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new MelCloudUnavailableException($"Could not reach MELCloud: {error.Message}", error);
            }
            catch (TimeoutException error)
            {
                throw new MelCloudUnavailableException(error.Message, error);
            }
        }

        /// <summary>Throws for a response that isn't a success: 429 and 5xx mean MELCloud is unavailable for now.</summary>
        internal static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new MelCloudUnavailableException($"MELCloud is limiting requests (429) for {what}; the next poll tries again.");
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 200)
                body = body[..200];
            throw new MelCloudUnavailableException($"MELCloud answered {(int)response.StatusCode} {response.ReasonPhrase} for {what}. {body}".TrimEnd());
        }

        internal static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException error)
            {
                throw new MelCloudUnavailableException($"MELCloud's answer for {what} is not JSON.", error);
            }
        }

        /// <summary>The e-mail with only its first letters, for logs.</summary>
        internal static string Mask(string email)
        {
            var at = email.IndexOf('@');
            return at <= 0 ? "***" : $"{email[0]}***@{(at + 1 < email.Length ? email[at + 1] : '*')}***";
        }
    }

    /// <summary>Lenient readers for MELCloud's JSON, which sends numbers and booleans as strings too.</summary>
    internal static class MelCloudJson
    {
        public static double? Number(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<double>(out var number))
                return number;
            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                return number;
            return null;
        }

        public static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<bool>(out var flag))
                return flag;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag))
                return flag;
            if (Number(node) is double number)
                return number != 0;
            return null;
        }

        public static string? Text(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<string>(out var text))
                return text;
            return Number(node)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static IEnumerable<JsonObject> Objects(JsonNode? node) =>
            node is JsonArray array ? array.OfType<JsonObject>() : [];
    }

    /// <summary>
    /// Keeps a <see cref="MelCloudSession"/> in <c>&lt;data&gt;/melcloud-&lt;cloud&gt;-&lt;account&gt;.json</c>; the account
    /// part is a hash of the e-mail, so the file name doesn't show it. The file holds the login, like the Netatmo token
    /// files: keep the data folder private.
    /// </summary>
    internal sealed class MelCloudSessionStore
    {
        private readonly ILogger _logger;

        public MelCloudSessionStore(string dataDirectory, string cloud, string email, ILogger logger)
        {
            _logger = logger;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
            File = Path.Combine(dataDirectory, $"melcloud-{cloud}-{Convert.ToHexStringLower(hash)[..16]}.json");
        }

        public string File { get; }

        public MelCloudSession? Load()
        {
            try
            {
                return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<MelCloudSession>(System.IO.File.ReadAllText(File)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(error, "Could not read the MELCloud session file {File}; logging in again.", File);
                return null;
            }
        }

        public void Save(MelCloudSession session)
        {
            try
            {
                var directory = Path.GetDirectoryName(File);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temporary = File + ".tmp";
                System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(session));
                System.IO.File.Move(temporary, File, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "Could not save the MELCloud session file {File}; the next start logs in again.", File);
            }
        }

        public void Delete()
        {
            try
            {
                System.IO.File.Delete(File);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "Could not delete the MELCloud session file {File}.", File);
            }
        }
    }
}
