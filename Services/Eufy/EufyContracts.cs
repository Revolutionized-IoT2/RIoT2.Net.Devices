using System.Text.Json;

// The eufy cloud client is a C# port of a subset of eufy-sdk (https://github.com/mega-yfue/eufy-sdk,
// Apache-2.0) at commit 5f92bea559fa1a5b619a84a9966ccc90a541735e. See NOTICE.

namespace RIoT2.Net.Devices.Services.Eufy
{
    /// <summary>The eufy account the client signs in with, and where it keeps its session files.</summary>
    public sealed record EufyAccountOptions
    {
        public required string Email { get; init; }
        public required string Password { get; init; }
        /// <summary>Two-letter account country code; routes the cloud region.</summary>
        public string CountryCode { get; init; } = "US";
        /// <summary>The folder for the session and push registration files.</summary>
        public string DataDirectory { get; init; } = "Data";
        /// <summary>The ffmpeg executable the live snapshot uses.</summary>
        public string FfmpegPath { get; init; } = "ffmpeg";
    }

    public enum EufyLoginStatus
    {
        Ok,
        Captcha,
        TwoFactor
    }

    /// <summary>The outcome of a login step.</summary>
    /// <param name="CaptchaImage">For <see cref="EufyLoginStatus.Captcha"/>: a <c>data:image/png;base64,</c> image.</param>
    /// <param name="Retry">For <see cref="EufyLoginStatus.Captcha"/>: the previous answer was wrong.</param>
    /// <param name="TwoFactorMethod">For <see cref="EufyLoginStatus.TwoFactor"/>: how the code was sent.</param>
    public sealed record EufyLoginResult(EufyLoginStatus Status, string? CaptchaImage = null, bool Retry = false, string? TwoFactorMethod = null);

    /// <summary>A station or device of the account, as the cloud lists it.</summary>
    public sealed class EufyDeviceRecord
    {
        public required string Serial { get; init; }
        public required string Name { get; init; }
        public string? Model { get; init; }
        /// <summary>The station the device's commands go through; its own serial for a station or a standalone camera.</summary>
        public required string StationSerial { get; init; }
        public int? Channel { get; init; }
        public string? P2pDid { get; init; }
        public int? DeviceType { get; init; }
        /// <summary>The cloud's last-known param values, by param type.</summary>
        public IReadOnlyDictionary<int, string> Params { get; init; } = new Dictionary<int, string>();
        /// <summary>The raw list entry.</summary>
        public JsonElement Raw { get; init; }
        public bool IsStation => Serial == StationSerial;
    }

    /// <summary>The eufy cloud gateway rejected the request.</summary>
    public class EufyCloudException : Exception
    {
        public EufyCloudException(string message, int? code = null, int? status = null, Exception? inner = null) : base(message, inner)
        {
            Code = code;
            Status = status;
        }

        public int? Code { get; }
        public int? Status { get; }
    }

    /// <summary>The session was rejected and could not be renewed; log in again.</summary>
    public class EufySessionExpiredException : EufyCloudException
    {
        public EufySessionExpiredException(string message, TimeSpan retryAfter = default) : base(message, status: 401)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan RetryAfter { get; }
    }

    /// <summary>The eufy cloud HTTP API (port of eufy-sdk <c>transport/http/mega-client.ts</c>).</summary>
    public interface IEufyCloudApi : IDisposable
    {
        bool IsAuthenticated { get; }
        /// <summary>The account's user id, once logged in.</summary>
        string? UserId { get; }
        /// <summary>The name written into commands that carry an actor (guard mode <c>user_name</c>).</summary>
        string AccountName { get; }

        /// <summary>Logs in, or restores the persisted session without a round trip when it is still valid.</summary>
        Task<EufyLoginResult> LoginAsync(CancellationToken cancellationToken);
        Task<EufyLoginResult> SolveCaptchaAsync(string answer, CancellationToken cancellationToken);
        Task<EufyLoginResult> SubmitVerifyCodeAsync(string code, CancellationToken cancellationToken);
        /// <summary>Forgets the persisted session.</summary>
        void ClearSession();

        /// <summary>Every station and device of the account (all houses, merged by serial).</summary>
        Task<IReadOnlyList<EufyDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken);

        /// <summary>Authenticated, signed and encrypted POST to <c>app-{service}-{region}.eufy.com{path}</c>; returns the decrypted <c>data</c>.</summary>
        Task<JsonElement> PostAsync(string service, string path, object? body, CancellationToken cancellationToken);
        /// <summary>Authenticated POST to the <c>security-app</c> (eufylife) gateway; returns the decrypted <c>data</c>.</summary>
        Task<JsonElement> SecurityAppPostAsync(string path, object? body, CancellationToken cancellationToken);

        /// <summary>The P2P DSK keys of the stations, by station serial.</summary>
        Task<IReadOnlyDictionary<string, (string DskKey, long Expiration)>> GetDskKeysAsync(IReadOnlyCollection<string> stationSerials, CancellationToken cancellationToken);
        /// <summary>The device's public key (<c>/v1/app/public_key/query</c>).</summary>
        Task<string> GetDevicePublicKeyAsync(string deviceSerial, CancellationToken cancellationToken);

        Task RegisterPushTokenAsync(string fcmToken, CancellationToken cancellationToken);
        /// <summary>Downloads a push image (thumbnail) and decrypts it when it is encrypted; returns JPEG bytes.</summary>
        Task<byte[]> DownloadImageAsync(string url, string? p2pDid, CancellationToken cancellationToken);
    }

    /// <summary>The security events the RIoT2 device reports.</summary>
    public enum EufyEventKind
    {
        Motion,
        PersonDetected,
        StrangerDetected,
        PetDetected,
        DogDetected,
        SoundDetected,
        CryingDetected,
        VehicleDetected,
        DoorbellPress,
        PackageDelivered,
        PackageTaken,
        PackageStranded,
        /// <summary>The guard mode changed; carries no value, re-read it.</summary>
        ArmingModeChanged,
        /// <summary>Station alarm; see <see cref="EufySecurityEvent.AlarmPhase"/>.</summary>
        Alarm
    }

    public enum EufyAlarmPhase
    {
        Triggered,
        Delayed,
        Stopped
    }

    /// <summary>A semantic event decoded from a push (port of eufy-sdk's capability <c>decodeEvent</c> for these kinds).</summary>
    public sealed record EufySecurityEvent
    {
        public required EufyEventKind Kind { get; init; }
        public string? DeviceSerial { get; init; }
        public string? StationSerial { get; init; }
        /// <summary>The push's thumbnail URL, when it carried one.</summary>
        public string? ThumbnailUrl { get; init; }
        /// <summary>A recognised person's name, when the push named one.</summary>
        public string? PersonName { get; init; }
        public EufyAlarmPhase? AlarmPhase { get; init; }
        /// <summary>The push's alarm source or stop reason (eufy <c>CusPushAlarmType</c>).</summary>
        public int? AlarmType { get; init; }
        /// <summary>The raw eufy push event type.</summary>
        public int? EventType { get; init; }
    }

    /// <summary>
    /// The FCM/MCS push channel (port of eufy-sdk <c>transport/push/*</c>): registers with FCM once (persisted),
    /// registers the token with the eufy cloud, keeps the MCS connection alive and raises decoded events.
    /// </summary>
    public interface IEufyPushClient : IAsyncDisposable
    {
        event Action<EufySecurityEvent>? EventReceived;
        /// <summary>Raised with true when the MCS connection is up, false when it drops.</summary>
        event Action<bool>? ConnectionChanged;
        bool IsConnected { get; }
        /// <summary>Registers (when needed) and connects; returns once the first connection is up. Reconnects on its own afterwards.</summary>
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
    }

    /// <summary>The station P2P channel (port of the eufy-sdk <c>transport/p2p</c> parts this device needs).</summary>
    public interface IEufyP2PClient : IAsyncDisposable
    {
        /// <summary>The stations and devices the client may connect to; call again after the device list changes.</summary>
        void UpdateDevices(IReadOnlyList<EufyDeviceRecord> devices);
        /// <summary>Sets the station's guard mode; <paramref name="wireMode"/> is the eufy value (0 away, 1 home, 2 schedule, 3-5 custom, 6 off, 47 geo, 63 disarmed).</summary>
        Task SetGuardModeAsync(string stationSerial, int wireMode, CancellationToken cancellationToken);
        /// <summary>Takes a fresh still from the camera's live stream; returns JPEG bytes.</summary>
        Task<byte[]> SnapshotAsync(string deviceSerial, CancellationToken cancellationToken);
        Task CloseAsync();
    }
}
