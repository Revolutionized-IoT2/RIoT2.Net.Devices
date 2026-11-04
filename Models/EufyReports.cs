namespace RIoT2.Net.Devices.Models
{
    /// <summary>The value of the Eufy Security device's account status report (<c>eufy|status</c>).</summary>
    public class EufyStatusReport
    {
        /// <summary>Stopped, Connecting, CaptchaRequired, TwoFactorRequired, Connected or Error.</summary>
        public string State { get; set; } = "";
        public string? Message { get; set; }
        /// <summary>The captcha to answer with the verify command, while one is pending.</summary>
        public string? CaptchaImageUrl { get; set; }
        /// <summary>How the verification code was sent, while one is pending.</summary>
        public string? TwoFactorMethod { get; set; }
    }

    /// <summary>The value of a station's alarm report (<c>&lt;station&gt;|alarm</c>).</summary>
    public class EufyAlarmReport
    {
        /// <summary>triggered, delayed or stopped.</summary>
        public string Phase { get; set; } = "";
        /// <summary>The alarm source or stop reason (eufy <c>CusPushAlarmType</c>), when the push carried one.</summary>
        public int? AlarmType { get; set; }
        public string? DeviceSerial { get; set; }
    }
}
