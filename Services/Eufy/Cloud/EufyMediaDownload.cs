// Ported from eufy-sdk src/transport/http/media-download.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>
    /// A push-media download that produced no bytes, tagged with why (<c>url-not-allowed</c>, <c>address-not-public</c>,
    /// <c>redirect-not-allowed</c>, <c>too-large</c>, <c>http-status</c>, <c>timeout</c>, <c>network</c>, <c>decode-failed</c>).
    /// The message never carries the URL, the response or a credential.
    /// </summary>
    internal sealed class EufyMediaException : EufyCloudException
    {
        public EufyMediaException(string message, string reason, int? status = null, Exception? inner = null) : base(message, status: status, inner: inner)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    /// <summary>The media host rejected the active session's credentials.</summary>
    internal sealed class EufyMediaAuthenticationException : Exception
    {
        public EufyMediaAuthenticationException() : base("Media authentication failed") { }
    }

    /// <summary>
    /// Downloads one bounded push-media resource over HTTPS from the exact eufy media hosts, following at most one
    /// redirect to an allowlisted object store, rejecting hosts that resolve to non-public addresses, and sending
    /// the authentication headers to the original eufy host only.
    /// </summary>
    internal static partial class EufyMediaDownload
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        public const long MaxBytes = 10 * 1024 * 1024;

        public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken cancellationToken);

        public static readonly HostResolver DefaultResolver = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

        [GeneratedRegex("^security-app(?:-(?:eu|ie))?\\.eufylife\\.com\\z", RegexOptions.CultureInvariant)]
        internal static partial Regex MediaHost();

        [GeneratedRegex("^zhixin-security-[a-z0-9]+(?:-[a-z0-9]+)*\\.s3(?:\\.[a-z]{2}(?:-[a-z0-9]+)+-[0-9])?\\.amazonaws\\.com\\z", RegexOptions.CultureInvariant)]
        internal static partial Regex ObjectHost();

        /// <summary>Parses a media URL and enforces HTTPS, no credentials, the default port, a DNS name and the host pattern.</summary>
        internal static Uri AllowedMediaUrl(string? value, Regex hostPattern, string reason)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
                throw new EufyMediaException("Media download rejected", reason);
            return AllowedMediaUrl(url, hostPattern, reason);
        }

        private static Uri AllowedMediaUrl(Uri url, Regex hostPattern, string reason)
        {
            if (!url.IsAbsoluteUri
                || url.Scheme != Uri.UriSchemeHttps
                || url.UserInfo.Length > 0
                || !url.IsDefaultPort
                || url.HostNameType is not UriHostNameType.Dns
                || !hostPattern.IsMatch(url.IdnHost.ToLowerInvariant()))
            {
                throw new EufyMediaException("Media download rejected", reason);
            }
            return url;
        }

        /// <summary>True for an address in a private, loopback, link-local, CGNAT, multicast or unspecified range.</summary>
        internal static bool IsBlocked(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                return b[0] == 0
                    || b[0] == 10
                    || (b[0] == 100 && (b[1] & 0xc0) == 64)
                    || b[0] == 127
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && (b[1] & 0xf0) == 16)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] & 0xf0) == 224;
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = address.GetAddressBytes();
                return address.Equals(IPAddress.IPv6Any)
                    || address.Equals(IPAddress.IPv6Loopback)
                    || (b[0] & 0xfe) == 0xfc
                    || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
            }
            return true;
        }

        private static async Task AssertPublicResolutionAsync(Uri url, HostResolver resolver, CancellationToken cancellationToken)
        {
            IPAddress[] addresses;
            try
            {
                addresses = await resolver(url.IdnHost, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new EufyMediaException("Media download rejected", "address-not-public");
            }
            if (addresses.Length == 0 || addresses.Any(IsBlocked))
                throw new EufyMediaException("Media download rejected", "address-not-public");
        }

        private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength is long declared && declared > MaxBytes)
                throw new EufyMediaException("Media download rejected", "too-large");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    throw new EufyMediaException("Media download rejected", "too-large");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Downloads <paramref name="url"/> with <paramref name="authenticatedHeaders"/>; the whole attempt, DNS included,
        /// is bounded by <paramref name="timeout"/> (15 s by default). The client must not follow redirects itself.
        /// </summary>
        public static async Task<byte[]> DownloadAsync(HttpClient http, string url, IReadOnlyDictionary<string, string> authenticatedHeaders,
            HostResolver resolver, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            var original = AllowedMediaUrl(url, MediaHost(), "url-not-allowed");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? Timeout);
            try
            {
                await AssertPublicResolutionAsync(original, resolver, cts.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, original);
                foreach (var (name, value) in authenticatedHeaders)
                    request.Headers.TryAddWithoutValidation(name, value);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null || !location.IsAbsoluteUri)
                        throw new EufyMediaException("Media download rejected", "redirect-not-allowed");
                    var target = AllowedMediaUrl(location, ObjectHost(), "redirect-not-allowed");
                    await AssertPublicResolutionAsync(target, resolver, cts.Token).ConfigureAwait(false);
                    using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, target);
                    using var redirected = await http.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    var redirectedStatus = (int)redirected.StatusCode;
                    if (redirectedStatus is >= 300 and < 400)
                        throw new EufyMediaException("Media download rejected", "redirect-not-allowed");
                    if (redirectedStatus != 200)
                        throw new EufyMediaException("Media download failed", "http-status", redirectedStatus);
                    return await ReadBodyAsync(redirected, cts.Token).ConfigureAwait(false);
                }
                if (status is 401 or 403)
                    throw new EufyMediaAuthenticationException();
                if (status != 200)
                    throw new EufyMediaException("Media download failed", "http-status", status);
                return await ReadBodyAsync(response, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is EufyMediaException or EufyMediaAuthenticationException)
            {
                throw;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                throw new EufyMediaException("Media download timed out", "timeout");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
            {
                throw new EufyMediaException("Media download failed", "network");
            }
        }
    }
}
