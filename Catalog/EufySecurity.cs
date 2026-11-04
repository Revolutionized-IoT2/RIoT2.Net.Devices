using Microsoft.Extensions.Logging;
using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Abstracts;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The parameters of the <see cref="EufySecurity"/> and <see cref="EufySecuritySdk"/> devices: the eufy account.</summary>
    public sealed class EufySecurityOptions
    {
        [DeviceParameter("email", Required = true, Description = "E-mail address of the eufy account")]
        public string Email { get; set; } = "";

        [DeviceParameter("password", Required = true, Secret = true, Description = "Password of the eufy account")]
        public string Password { get; set; } = "";

        [DeviceParameter("countryCode", Default = "US", Description = "Two-letter country code of the eufy account; selects the cloud region")]
        public string CountryCode { get; set; } = "";

        [DeviceParameter("ffmpegPath", Default = "ffmpeg", Description = "The ffmpeg executable the snapshot command uses")]
        public string FfmpegPath { get; set; } = "";
    }

    /// <summary>
    /// A eufy account through the C# port of eufy-sdk (see <c>Services/Eufy</c>): connects to the eufy cloud and the
    /// stations itself, with no eufy-security-ws container.
    /// </summary>
    [RequiresHardware("eufy cloud account")]
    public class EufySecurity : EufySecurityBase
    {
        public EufySecurity(ILogger logger, IEufySecurityService eufySecurityService, IMemoryStorageService memoryStorageService)
            : base(logger, eufySecurityService, memoryStorageService) { }

        protected override string TemplateName => "Eufy Security";
    }
}
