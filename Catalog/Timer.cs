using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Catalog
{
    internal class Timer : DeviceBase, IRefreshableReportDevice
    {
        internal Timer(ILogger logger) : base(logger) { }

        public override void ConfigureDevice()
        {
            Configuration.RefreshSchedule = null; //always force this devices schedule to null -> use report template schedules instead!
        }

        public void Start()
        {

        }
        public void Stop()
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
