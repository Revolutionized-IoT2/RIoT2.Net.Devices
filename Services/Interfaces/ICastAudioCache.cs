namespace RIoT2.Net.Devices.Services.Interfaces
{
    /// <summary>
    /// Speech audio the GoogleCast device made, kept in memory for a while so that a speaker can download it from
    /// <c>GET /api/cast/audio/{token}</c>. The token is random; the key names the speech, so a repeated phrase reuses it.
    /// </summary>
    public interface ICastAudioCache
    {
        /// <summary>The token of the audio stored under <paramref name="key"/>, or null.</summary>
        string? Find(string key);

        /// <summary>Stores MP3 audio under <paramref name="key"/> and returns its token.</summary>
        string Add(string key, byte[] audio);

        /// <summary>The audio of a token, or null when it's unknown or has expired.</summary>
        byte[]? Get(string token);
    }
}
