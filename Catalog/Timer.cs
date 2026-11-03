using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>Sends each report template's <c>address</c> as its value when the template's schedule fires.</summary>
    public class Timer : AsyncDeviceBase
    {
        public Timer(ILogger logger) : base(logger) { }

        public override void ConfigureDevice()
        {
            Configuration.RefreshSchedule = null; //always force this devices schedule to null -> use report template schedules instead!
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken)
        {
            if (report != null)
            {
                SendReport(this, new Report()
                {
                    Id = report.Id,
                    TimeStamp = DateTime.UtcNow.ToEpoch(),
                    Value = new ValueModel(report.Address),
                    Filter = ""
                });
            }
            return Task.CompletedTask;
        }
    }
}
