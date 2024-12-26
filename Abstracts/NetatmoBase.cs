using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core;
using System.Net;
using RIoT2.Net.Devices.Models;
using RIoT2.Core.Models;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Devices.Abstracts
{
    internal abstract class NetatmoBase : DeviceBase
    {
        private static string _accessToken = "";
        private static string _refreshToken = "";
        private static string _clientID = "";
        private static string _clientSecret = "";
        private static string _authUrl = "https://api.netatmo.com/oauth2/token";
        private static bool _configured = false;
        private const string _authDataFile = "Data/netatmoAuth.json";
        private static ILogger _logger;

        public NetatmoBase(ILogger logger) : base(logger) 
        {
            _logger = logger;
        }

        internal void SendNetatmoReport(IDevice device, string parameter, dynamic value)
        {
            if (value == null)
                return;

            var template = ReportTemplates.FirstOrDefault(x => x.Address.ToLower() == parameter);
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

        internal static void ConfigureNetatmo(string token, string refreshToken, string clientId, string clientSecret) 
        {
            if (_configured)
                return;

            var storedAuth = LoadNetatmoAuth();
            if (storedAuth != null) 
            {
                _accessToken = storedAuth.access_token;
                _refreshToken = storedAuth.refresh_token;
            }
            else 
            {
                _accessToken = token;
                _refreshToken = refreshToken;
                
                //save to local 
                SaveNetatmoAuth(new NetatmoAuth { 
                    access_token = _accessToken,
                    refresh_token = _refreshToken
                });
            }

            _clientID = clientId;
            _clientSecret = clientSecret;
            _configured = true;
        }

        private static async Task<bool> refreshAuthentication() 
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers.Add("content-type", "application/x-www-form-urlencoded");

            string postContent = $"grant_type=refresh_token&refresh_token={_refreshToken}&client_id={_clientID}&client_secret={_clientSecret}";
            var response = await Web.PostAsync(_authUrl, postContent, headers);
            if (response != null && response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync();
                var auth = content.ToObj<NetatmoAuth>();
                if(auth != null) 
                {
                    SaveNetatmoAuth(auth);
                    _accessToken = auth.access_token;
                    _refreshToken = auth.refresh_token;
                    return true;
                }
            }
            else 
            {
                _logger.LogWarning($"Could not refresh Netatmo token: {_refreshToken}. Message: {await response?.Content.ReadAsStringAsync()}");
            }

            SaveNetatmoAuth(null); //save null -> delete local authentication. Then tokens from configuration will be used
            return false;
        }

        private static NetatmoAuth LoadNetatmoAuth() 
        {
            try
            {
                if (!File.Exists(_authDataFile)) 
                {
                    _logger.LogWarning("Netatmo auth file does not exist");
                    return null;
                }
                    
                var json = File.ReadAllText(_authDataFile)?.ToObj<NetatmoAuth>();
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Could not load Netatmo authentication file");
                throw new Exception("Could not load Netatmo authentication file");
            }
            return null;
        }

        private static void SaveNetatmoAuth(NetatmoAuth auth) 
        {
            try
            {
                if (auth == null) 
                {
                    File.Delete(_authDataFile);
                    return;
                }

                using (TextWriter writer = new StreamWriter(_authDataFile, false))
                {
                    writer.Write(auth.ToJson());
                    writer.Flush();
                    writer.Close();
                }
            }
            catch (Exception x)
            {
                _logger.LogError(x, $"Could not save Netatmo auth file {_authDataFile}");
                throw new Exception("Could not save Netatmo auth file");
            }
        }

        /*
        //This is now obsolete in Netatmo API
        private static async Task<bool> authenticate()
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers.Add("content-type", "application/x-www-form-urlencoded");
            string scopes = "read_station read_camera write_camera access_camera read_presence access_presence";

            string postContent = $"grant_type=password&client_id={_clientID}&client_secret={_clientSecret}&username={_userName}&password={_password}&scope={scopes}";
            var response = await Common.Web.Instance.PostAsync(_authUrl, postContent, headers);
            if (response != null && response.StatusCode == HttpStatusCode.OK)
            {
                var content = await response.Content.ReadAsStringAsync();
                _accessToken = content.ToObj<NetatmoAuth>().access_token;
                return true;
            }
            return false;
        }*/

        internal async Task<NetatmoStationsData> GetNetatmoStationsData(string deviceId) 
        {
            return await executeNetatmoGET<NetatmoStationsData>($"https://api.netatmo.com/api/getstationsdata?device_id={ System.Web.HttpUtility.UrlEncode(deviceId) }");
        }

        internal async Task<NetatmoHomesData> GetNetatmoHomesData()
        {
            return await executeNetatmoGET<NetatmoHomesData>("https://api.netatmo.com/api/homesdata");
        }

        internal async Task<NetatmoHomeStatus> GetNetatmoHomeStatus(string homeId) 
        {
            return await executeNetatmoGET<NetatmoHomeStatus>($"https://api.netatmo.com/api/homestatus?home_id={ System.Web.HttpUtility.UrlEncode(homeId) }");
        }

        internal async Task<NetatmoEvent> GetNetatmoEvents(string homeId)
        {
            return await executeNetatmoGET<NetatmoEvent>($"https://api.netatmo.com/api/getevents?home_id={ System.Web.HttpUtility.UrlEncode(homeId) }");
        }

        private async Task<T> executeNetatmoGET<T>(string url)
        {
            if (String.IsNullOrEmpty(_accessToken)) 
            {
                _logger.LogWarning("Cannot execute Netatmo command. Missing access token.");
                return default;
            }

            try 
            {
                var response = await Web.GetResponseAsync(url, _accessToken);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (await refreshAuthentication())
                        response = await Web.GetResponseAsync(url, _accessToken);
                    else
                    {
                        _logger.LogWarning("Could not refresh Netatmo authentication token");
                    }
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return content.ToObj<T>();
                }
                else
                {
                    _logger.LogWarning($"Netatmo responded with code: {response.StatusCode}. Message: {await response.Content.ReadAsStringAsync()}. Token: {_accessToken}");
                }

            }
            catch (Exception x) 
            {
                _logger.LogError(x, "Error while getting data from Netatmo");
                throw new Exception("Error while getting data from Netatmo");
            }

            return default;
        }
    }
}
