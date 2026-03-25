using RIoT2.Core;
using RIoT2.Net.Devices.Models;
using RIoT2.Net.Devices.Services.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace RIoT2.Net.Devices.Services
{
    public class ApSystemsClientService : IApSystemsClientService
    {
        private HttpClient _httpClient;
        private string _appId;
        private string _appSecret;
        private string _sid; // Your System ID
        private string BaseUrl = "https://api.apsystemsema.com:9282";

        /// <summary>
        /// Initializes a new instance of the ApSystemsClient.
        /// </summary>
        /// <param name="appId">Provided by APsystems after API access approval.</param>
        /// <param name="appSecret">Provided by APsystems.</param>
        /// <param name="sid">System ID found in your EMA portal.</param>
        /// <param name="httpClient">Optional injected HttpClient instance.</param>
        public void Configure(string appId, string appSecret, string sid)
        {
            _appId = appId ?? throw new ArgumentNullException(nameof(appId));
            _appSecret = appSecret ?? throw new ArgumentNullException(nameof(appSecret));
            _sid = sid ?? throw new ArgumentNullException(nameof(sid));
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Gets the details and current production data for the configured system.
        /// </summary>
        public async Task<string> GetSystemDetailsAsync()
        {
            // The API specific route for system details
            var requestPath = $"/user/api/v2/systems/details/{_sid}";

            return await SendRequestAsync(HttpMethod.Get, requestPath);
        }

        public async Task<string> GetSystemMetersAsync()
        {
            // The API specific route for system meters data
            var requestPath = $"/user/api/v2/systems/meters/{_sid}";
            return await SendRequestAsync(HttpMethod.Get, requestPath);
        }

        public async Task<string> GetSystemSummaryAsync()
        {
            // The API specific route for system summary data
            var requestPath = $"/user/api/v2/systems/summary/{_sid}";
            return await SendRequestAsync(HttpMethod.Get, requestPath);
        }

        public async Task<string> GetEcuSummaryAsync(string eid)
        {
            // The API specific route for ECU summary data
            var requestPath = $"/user/api/v2/systems/{_sid}/devices/ecu/summary/{eid}";
            return await SendRequestAsync(HttpMethod.Get, requestPath);
        }

        public async Task<MeterSummary> GetMeterSummaryAsync(string ecuid)
        {
            // The API specific route for meter summary data
            var requestPath = $"/user/api/v2/systems/{_sid}/devices/meter/summary/{ecuid}";
            var summaryJson = await SendRequestAsync(HttpMethod.Get, requestPath);
            return summaryJson.ToObj<MeterSummary>();
        }

        /// <summary>
        /// Base method to sign and send requests to the APsystems EMA API.
        /// </summary>
        private async Task<string> SendRequestAsync(HttpMethod method, string requestPath)
        {
            // 1. Generate required signing properties
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var nonce = Guid.NewGuid().ToString("N"); // Convert.ToHexString(Guid.NewGuid().ToByteArray());
            var signatureMethod = "HmacSHA256";
            var path = requestPath.Split("/").Last();

            // 2. Format the string exactly as the API requires
            var stringToSign = $"{timestamp}/{nonce}/{_appId}/{path}/{method.Method.ToUpper()}/{signatureMethod}";

            // 3. Compute the HMAC signature
            var signature = ComputeHmacSha256(stringToSign, _appSecret);

            // 4. Prepare the request and append required headers
            using var request = new HttpRequestMessage(method, BaseUrl + requestPath);
            //request.Headers.Add("Content-Type", "application/json");
            request.Headers.Add("X-CA-Timestamp", timestamp);
            request.Headers.Add("X-CA-Nonce", nonce);
            request.Headers.Add("X-CA-AppId", _appId);
            request.Headers.Add("X-CA-Signature-Method", signatureMethod);
            request.Headers.Add("X-CA-Signature", signature);

            // 5. Send the request
            using var response = await _httpClient.SendAsync(request);

            // Throw an exception if the HTTP status code is not 2xx
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Computes an HMAC-SHA256 hash and returns it as a Base64 string.
        /// </summary>
        private static string ComputeHmacSha256(string data, string secret)
        {
            var encoding = new UTF8Encoding();
            var keyBytes = encoding.GetBytes(secret);
            var dataBytes = encoding.GetBytes(data);

            using var hmac = new HMACSHA256(keyBytes);
            var hashBytes = hmac.ComputeHash(dataBytes);

            return Convert.ToBase64String(hashBytes);
        }
    }
}
