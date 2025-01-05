using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Catalog
{
    public class Timer : DeviceBase, IRefreshableReportDevice
    {
        public Timer(ILogger logger) : base(logger) { }

        public override void ConfigureDevice()
        {
            Configuration.RefreshSchedule = null; //always force this devices schedule to null -> use report template schedules instead!
        }

        public override void StartDevice()
        {

        }
        public override void StopDevice()
        {

        }

        public override void Refresh(ReportTemplate report)
        {
            if (report == null)
                return;

            SendReport(this, new Report()
            {
                Id = report.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(report.Address),
                Filter = ""
            });
        }
    }
}
