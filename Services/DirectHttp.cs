using System.Net;
using RIoT2.Core.Security;

namespace RIoT2.Net.Devices.Services
{
    /// <summary>
    /// An HTTP client for cloud logins that handle redirects and cookies themselves: it follows no redirects, keeps no
    /// cookies, and checks every request against the node's outbound URL policy. Used by the MELCloud, Miele, DEFA Power
    /// and Harvia devices.
    /// </summary>
    internal static class DirectHttp
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

        /// <summary>
        /// Sends <paramref name="request"/>; a service that doesn't answer in <see cref="Timeout"/> throws
        /// <see cref="TimeoutException"/>. With <see cref="HttpCompletionOption.ResponseHeadersRead"/> the timeout covers
        /// only the headers, so an event stream can stay open.
        /// </summary>
        public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string service, CancellationToken cancellationToken,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                return await _client.SendAsync(request, completion, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{service} did not answer in {Timeout.TotalSeconds:0} seconds.", error);
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
    }
}
