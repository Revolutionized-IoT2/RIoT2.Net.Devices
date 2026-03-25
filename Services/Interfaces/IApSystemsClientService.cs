using RIoT2.Net.Devices.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public interface IApSystemsClientService
    {
        void Configure(string appId, string appSecret, string sid);
        Task<MeterSummary> GetMeterSummaryAsync(string ecuid);
    }
}
