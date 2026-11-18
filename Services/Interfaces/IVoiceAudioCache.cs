namespace RIoT2.Net.Devices.Services.Interfaces
{
    /// <summary>
    /// Speech the VoiceSatellite device made, kept in memory for a while so that the satellite can download it from
    /// <c>GET /api/voice/audio/{token}</c>. The token is random.
    /// </summary>
    public interface IVoiceAudioCache
    {
        /// <summary>Stores a WAV file and returns its token.</summary>
        string Add(byte[] wav);

        /// <summary>The WAV file of a token, or null when it's unknown or has expired.</summary>
        byte[]? Get(string token);
    }
}
