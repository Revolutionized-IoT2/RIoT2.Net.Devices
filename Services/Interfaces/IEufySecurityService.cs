using RIoT2.Net.Devices.Services.Eufy;

namespace RIoT2.Net.Devices.Services.Interfaces
{
    public enum EufyConnectionState
    {
        Stopped,
        Connecting,
        /// <summary>The cloud asks for a captcha; answer it with <see cref="IEufySecurityService.SubmitVerificationAsync"/>.</summary>
        CaptchaRequired,
        /// <summary>A verification code was sent; answer it with <see cref="IEufySecurityService.SubmitVerificationAsync"/>.</summary>
        TwoFactorRequired,
        Connected,
        Error
    }

    /// <param name="CaptchaImage">For <see cref="EufyConnectionState.CaptchaRequired"/>: a <c>data:image/png;base64,</c> image.</param>
    public sealed record EufyServiceStatus(EufyConnectionState State, string? Message = null, string? CaptchaImage = null, string? TwoFactorMethod = null);

    /// <summary>
    /// The eufy account: cloud login, station and device list, push events and station commands over P2P. Replaces the
    /// external eufy-security-ws service.
    /// </summary>
    public interface IEufySecurityService
    {
        EufyServiceStatus Status { get; }
        /// <summary>The stations and devices of the last device list.</summary>
        IReadOnlyList<EufyDeviceRecord> Devices { get; }

        /// <summary>A detection, doorbell, package or alarm event, with the thumbnail it carried as JPEG bytes when it had one.</summary>
        event Action<EufySecurityEvent, byte[]?>? SecurityEvent;
        /// <summary>A station's guard mode (eufy wire value) changed or was read for the first time.</summary>
        event Action<string, int>? GuardModeChanged;
        event Action<EufyServiceStatus>? StatusChanged;

        void Configure(EufyAccountOptions options);
        /// <summary>The station's last known guard mode (eufy wire value).</summary>
        int? GetGuardMode(string stationSerial);

        /// <summary>
        /// Logs in and connects. A pending captcha or verification code is reported through <see cref="Status"/>, not
        /// thrown; a rejected login or an unreachable cloud throws.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
        /// <summary>Answers the pending captcha or verification code, then connects.</summary>
        Task SubmitVerificationAsync(string answer, CancellationToken cancellationToken);
        /// <summary>Reads the device list again and raises <see cref="GuardModeChanged"/> for the guard modes that changed.</summary>
        Task RefreshAsync(CancellationToken cancellationToken);
        /// <param name="mode">The eufy wire value: 0 away, 1 home, 2 schedule, 3-5 custom, 6 off, 47 geofence, 63 disarmed.</param>
        Task SetGuardModeAsync(string stationSerial, int mode, CancellationToken cancellationToken);
        /// <summary>Takes a still from the camera's live stream (needs ffmpeg); returns JPEG bytes.</summary>
        Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken);
    }
}
