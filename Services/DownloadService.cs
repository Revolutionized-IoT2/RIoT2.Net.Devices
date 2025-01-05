using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services
{
    public class DownloadService : IDownloadService
    {
        private string _baseUrl = "";

        public string GetDownloadUrl(string filename)
        {
            return _baseUrl + filename;
        }

        public void SetBaseUrl(string url)
        {
            _baseUrl = url;
            if (!_baseUrl.EndsWith("/"))
                _baseUrl += "/";
        }
    }
}
