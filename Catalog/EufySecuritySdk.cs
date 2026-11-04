using Microsoft.Extensions.Logging;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>
    /// A eufy account through eufy-sdk itself (@mega-yfue/eufy-sdk), which runs in a Node.js child process
    /// (see <c>Services/EufySdk</c>). Its parameters, templates, reports and commands are those of
    /// <see cref="EufySecurity"/>, so the two are interchangeable.
    /// </summary>
    [RequiresHardware("eufy cloud account and Node.js")]
    public class EufySecuritySdk : EufySecurityBase
    {
        public EufySecuritySdk(ILogger logger, IEufySdkService eufySdkService, IMemoryStorageService memoryStorageService)
            : base(logger, eufySdkService, memoryStorageService) { }

        protected override string TemplateName => "Eufy Security (eufy-sdk)";
    }
}
