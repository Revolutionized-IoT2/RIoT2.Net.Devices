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

namespace RIoT2.Net.Devices.Abstracts
{
    public abstract class NetatmoBase : DeviceBase
    {
        private readonly INetatmoAuthClientFactory _authClients;
        private NetatmoAuthClient _auth;

        protected NetatmoBase(ILogger logger, INetatmoAuthClientFactory authClients) : base(logger)
        {
            _authClients = authClients ?? throw new ArgumentNullException(nameof(authClients));
        }

        internal void SendNetatmoReport(IDevice device, string parameter, dynamic value)
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
        internal void ConfigureNetatmo(string token, string refreshToken, string clientId, string clientSecret)
        {
            var auth = _authClients.Get(clientId);
            auth.Configure(token, refreshToken, clientSecret);
            _auth = auth;
        }

        internal async Task<NetatmoStationsData> GetNetatmoStationsData(string deviceId) 
        {
            return await executeNetatmo<NetatmoStationsData>($"https://api.netatmo.com/api/getstationsdata?device_id={ System.Web.HttpUtility.UrlEncode(deviceId) }");
        }

        internal async Task<NetatmoHomesData> GetNetatmoHomesData()
        {
            return await executeNetatmo<NetatmoHomesData>("https://api.netatmo.com/api/homesdata");
        }

        internal async Task<NetatmoHomeStatus> GetNetatmoHomeStatus(string homeId) 
        {
            return await executeNetatmo<NetatmoHomeStatus>($"https://api.netatmo.com/api/homestatus?home_id={ System.Web.HttpUtility.UrlEncode(homeId) }");
        }

        internal async Task<NetatmoEvent> GetNetatmoEvents(string homeId)
        {
            return await executeNetatmo<NetatmoEvent>($"https://api.netatmo.com/api/getevents?home_id={ System.Web.HttpUtility.UrlEncode(homeId) }");
        }

        internal async Task<NetatmoCmd> SetPersonsAway(string homeId)
        {
            return await executeNetatmo<NetatmoCmd>($"https://api.netatmo.com/api/setpersonsaway?home_id={System.Web.HttpUtility.UrlEncode(homeId)}", "POST");
        }
        internal async Task<NetatmoCmd> SetPersonsHome(string homeId, string[] personIds)
        {
            if (personIds == null || personIds.Length == 0) 
            {
                Logger.LogWarning("No person IDs provided. Cannot set persons home.");
                return await Task.FromResult<NetatmoCmd>(null);
            }

            var personIdsParam = string.Join("&", personIds.Select(x => $"person_id[]={System.Web.HttpUtility.UrlEncode(x)}"));
            return await executeNetatmo<NetatmoCmd>($"https://api.netatmo.com/api/setpersonshome?home_id={System.Web.HttpUtility.UrlEncode(homeId)}&{personIdsParam}", "POST");
        }

        private async Task<T> executeNetatmo<T>(string url, string method = "GET")
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
                var response = await sendNetatmo(url, method, accessToken);
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (await auth.RefreshAsync(accessToken))
                        response = await sendNetatmo(url, method, auth.AccessToken);
                    else
                        Logger.LogWarning("Could not refresh Netatmo authentication token");
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return content.ToObj<T>();
                }

                Logger.LogWarning("Netatmo responded with code: {StatusCode}. Message: {Message}", response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (Exception x)
            {
                Logger.LogError(x, "Error while getting data from Netatmo");
                throw new Exception("Error while getting data from Netatmo");
            }

            return default;
        }

        private static Task<HttpResponseMessage> sendNetatmo(string url, string method, string accessToken) =>
            method.Equals("get", StringComparison.OrdinalIgnoreCase)
                ? Web.GetResponseAsync(url, accessToken)
                : Web.PostWithBearerTokenAsync(url, null, accessToken);
    }
}
