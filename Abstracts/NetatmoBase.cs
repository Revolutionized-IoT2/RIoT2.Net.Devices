using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core;
using System.Net;
using RIoT2.Net.Devices.Models;
using RIoT2.Core.Models;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Services;
using RIoT2.Net.Devices.Services.Interfaces;
using System.Net.Http.Headers;

namespace RIoT2.Net.Devices.Abstracts
{
    public abstract class NetatmoBase : AsyncDeviceBase
    {
        private readonly INetatmoAuthClientFactory _authClients;
        private NetatmoAuthClient? _auth;

        protected NetatmoBase(ILogger logger, INetatmoAuthClientFactory authClients) : base(logger)
        {
            _authClients = authClients ?? throw new ArgumentNullException(nameof(authClients));
        }

        internal void SendNetatmoReport(IDevice device, string? parameter, dynamic? value)
        {
            if (value == null || parameter == null)
                return;

            var template = ReportTemplates?.FirstOrDefault(x => x.Address?.ToLower() == parameter);
            if (template == null)
                return;

            double threshold = 0;
            if (template.Parameters.ContainsKey("Threshold"))
                double.TryParse(template.Parameters["Threshold"], out threshold);

            SendReportIfValueChanged(device, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(value),
                Filter = ""
            }, threshold);
        }

        /// <summary>Uses the shared token client of <paramref name="clientId"/>; devices of the same Netatmo app share tokens.</summary>
        internal void ConfigureNetatmo(string? token, string? refreshToken, string? clientId, string? clientSecret)
        {
            var auth = _authClients.Get(clientId);
            auth.Configure(token, refreshToken, clientSecret);
            _auth = auth;
        }

        internal Task<NetatmoStationsData?> GetNetatmoStationsData(string? deviceId, CancellationToken cancellationToken = default) =>
            executeNetatmo<NetatmoStationsData>($"https://api.netatmo.com/api/getstationsdata?device_id={ System.Web.HttpUtility.UrlEncode(deviceId) }", HttpMethod.Get, cancellationToken);

        internal Task<NetatmoHomesData?> GetNetatmoHomesData(CancellationToken cancellationToken = default) =>
            executeNetatmo<NetatmoHomesData>("https://api.netatmo.com/api/homesdata", HttpMethod.Get, cancellationToken);

        internal Task<NetatmoHomeStatus?> GetNetatmoHomeStatus(string homeId, CancellationToken cancellationToken = default) =>
            executeNetatmo<NetatmoHomeStatus>($"https://api.netatmo.com/api/homestatus?home_id={ System.Web.HttpUtility.UrlEncode(homeId) }", HttpMethod.Get, cancellationToken);

        internal Task<NetatmoEvent?> GetNetatmoEvents(string homeId, CancellationToken cancellationToken = default) =>
            executeNetatmo<NetatmoEvent>($"https://api.netatmo.com/api/getevents?home_id={ System.Web.HttpUtility.UrlEncode(homeId) }", HttpMethod.Get, cancellationToken);

        internal Task<NetatmoCmd?> SetPersonsAway(string homeId, CancellationToken cancellationToken = default) =>
            executeNetatmo<NetatmoCmd>($"https://api.netatmo.com/api/setpersonsaway?home_id={System.Web.HttpUtility.UrlEncode(homeId)}", HttpMethod.Post, cancellationToken);

        internal Task<NetatmoCmd?> SetPersonsHome(string homeId, string[] personIds, CancellationToken cancellationToken = default)
        {
            if (personIds == null || personIds.Length == 0) 
            {
                Logger.LogWarning("No person IDs provided. Cannot set persons home.");
                return Task.FromResult<NetatmoCmd?>(null);
            }

            var personIdsParam = string.Join("&", personIds.Select(x => $"person_id[]={System.Web.HttpUtility.UrlEncode(x)}"));
            return executeNetatmo<NetatmoCmd>($"https://api.netatmo.com/api/setpersonshome?home_id={System.Web.HttpUtility.UrlEncode(homeId)}&{personIdsParam}", HttpMethod.Post, cancellationToken);
        }

        // The HTTP call; the lifecycle tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Web.SendAsync(request, cancellationToken: cancellationToken);

        private async Task<T?> executeNetatmo<T>(string url, HttpMethod method, CancellationToken cancellationToken)
        {
            var auth = _auth;
            var accessToken = auth?.AccessToken;
            if (String.IsNullOrEmpty(accessToken))
            {
                Logger.LogWarning("Cannot execute Netatmo command. Missing access token.");
                return default;
            }

            try
            {
                var response = await sendNetatmo(url, method, accessToken, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // accessToken came from auth, so auth is set.
                    if (await auth!.RefreshAsync(accessToken).ConfigureAwait(false))
                    {
                        response.Dispose();
                        response = await sendNetatmo(url, method, auth.AccessToken, cancellationToken).ConfigureAwait(false);
                    }
                    else
                        Logger.LogWarning("Could not refresh Netatmo authentication token");
                }

                using (response)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return content.ToObj<T>();

                    Logger.LogWarning("Netatmo responded with code: {StatusCode}. Message: {Message}", response.StatusCode, content);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Error while getting data from Netatmo");
                throw new Exception("Error while getting data from Netatmo");
            }

            return default;
        }

        // A fresh request per send: the retry after a token refresh can't reuse one.
        private async Task<HttpResponseMessage> sendNetatmo(string url, HttpMethod method, string accessToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (method == HttpMethod.Post)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
