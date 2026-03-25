using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IApSystemsClientService
    {
        void Configure(string appId, string appSecret, string sid);
        Task<MeterSummary> GetMeterSummaryAsync(string ecuid);
    }
}
