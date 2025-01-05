namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IDownloadService
    {
        void SetBaseUrl(string url);
        string GetDownloadUrl(string filename);
    }
}