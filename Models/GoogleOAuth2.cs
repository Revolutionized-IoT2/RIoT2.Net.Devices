using Google.Apis.Auth.OAuth2;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Devices.Models
{
    internal class GoogleOAuth2
    {
        private bool _authenticated = false;
        private ServiceAccountCredential _credentials;
        private DateTime _expires;
        private string _token;

        internal GoogleOAuth2(string serviceAccountJson)
        {
            _expires = DateTime.Now;
            if (String.IsNullOrEmpty(serviceAccountJson))
                return;

            try
            {
                var cr = Json.Deserialize<ServiceAccount>(serviceAccountJson);
                _credentials = new ServiceAccountCredential(new ServiceAccountCredential.Initializer(cr.client_email)
                {
                    Scopes = new[] {
                    "https://www.googleapis.com/auth/firebase.messaging",
                    "https://www.googleapis.com/auth/sdm.service"
                }
                }.FromPrivateKey(cr.private_key));
            }
            catch (Exception x)
            {
                throw new Exception("Error in GoogleOAuth2", x);
            }
        }

        internal bool IsAuthenticated { get { return _authenticated; } }

        internal async Task<string> GetToken()
        {
            if (_credentials == null)
                return null;

            if (!String.IsNullOrEmpty(_token) && _expires > DateTime.Now)
                return _token;

            _token = await _credentials.GetAccessTokenForRequestAsync();
            if (_credentials.Token == null)
                return null;

            _expires = _credentials.Token.ExpiresInSeconds.HasValue ? DateTime.Now.AddSeconds(_credentials.Token.ExpiresInSeconds.Value) : DateTime.Now;
            _authenticated = true;

            return _token;
        }
    }
}
