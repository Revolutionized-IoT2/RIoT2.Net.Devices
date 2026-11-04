using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RIoT2.Net.Devices.Services.Interfaces;

namespace RIoT2.Net.Devices.Services.GoogleCast
{
    /// <summary>
    /// The in-memory <see cref="ICastAudioCache"/>: an entry lives <see cref="TimeToLive"/> after it was last stored or
    /// found, and the oldest entries go when there are more than <see cref="MaxEntries"/> or <see cref="MaxBytes"/>.
    /// </summary>
    public sealed partial class CastAudioCache : ICastAudioCache
    {
        public static readonly TimeSpan TimeToLive = TimeSpan.FromHours(1);
        public const int MaxEntries = 64;
        public const long MaxBytes = 16 * 1024 * 1024;

        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _byToken = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
        private long _bytes;

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        /// <summary>A token is 32 lowercase hexadecimal digits; anything else is never looked up.</summary>
        public static bool IsToken(string? token) => token != null && TokenPattern().IsMatch(token);

        public string? Find(string key)
        {
            lock (_sync)
            {
                Expire();
                if (!_byKey.TryGetValue(key, out var entry))
                    return null;
                entry.Touched = Time.GetUtcNow();
                return entry.Token;
            }
        }

        public string Add(string key, byte[] audio)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(audio);
            if (audio.LongLength > MaxBytes)
                throw new ArgumentException("The audio is too large to keep.", nameof(audio));

            lock (_sync)
            {
                if (_byKey.TryGetValue(key, out var existing))
                    Remove(existing);

                var entry = new Entry(key, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), audio) { Touched = Time.GetUtcNow() };
                _byKey[key] = entry;
                _byToken[entry.Token] = entry;
                _bytes += audio.LongLength;

                Expire();
                while (_byToken.Count > MaxEntries || _bytes > MaxBytes)
                    Remove(_byToken.Values.Where(x => x != entry).MinBy(x => x.Touched) ?? entry);
                return entry.Token;
            }
        }

        public byte[]? Get(string token)
        {
            if (!IsToken(token))
                return null;
            lock (_sync)
            {
                Expire();
                return _byToken.TryGetValue(token, out var entry) ? entry.Audio : null;
            }
        }

        private void Expire()
        {
            var oldest = Time.GetUtcNow() - TimeToLive;
            foreach (var entry in _byToken.Values.Where(x => x.Touched < oldest).ToList())
                Remove(entry);
        }

        private void Remove(Entry entry)
        {
            if (!_byToken.Remove(entry.Token))
                return;
            _byKey.Remove(entry.Key);
            _bytes -= entry.Audio.LongLength;
        }

        [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
        private static partial Regex TokenPattern();

        private sealed class Entry(string key, string token, byte[] audio)
        {
            public string Key { get; } = key;
            public string Token { get; } = token;
            public byte[] Audio { get; } = audio;
            public DateTimeOffset Touched { get; set; }
        }
    }
}
