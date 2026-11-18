using RIoT2.Net.Devices.Services.GoogleCast;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services.Voice
{
    /// <summary>
    /// The in-memory <see cref="IVoiceAudioCache"/>, with the expiry and limits of <see cref="CastAudioCache"/>: an hour,
    /// at most 64 files and 16 MiB. Every reply is stored under a key of its own.
    /// </summary>
    public sealed class VoiceAudioCache : IVoiceAudioCache
    {
        private readonly CastAudioCache _cache = new();

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get => _cache.Time; set => _cache.Time = value; }

        public string Add(byte[] wav) => _cache.Add(Guid.NewGuid().ToString("N"), wav);

        public byte[]? Get(string token) => _cache.Get(token);
    }
}
