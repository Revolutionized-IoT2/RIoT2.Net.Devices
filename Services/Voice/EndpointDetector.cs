namespace RIoT2.Net.Devices.Services.Voice
{
    /// <summary>What <see cref="EndpointDetector"/> made of the audio so far.</summary>
    public enum EndpointResult
    {
        /// <summary>Still listening.</summary>
        Listening,

        /// <summary>The speaker stopped: the request is complete.</summary>
        Ended,

        /// <summary>Nobody spoke.</summary>
        NoSpeech
    }

    /// <summary>The <see cref="EndpointDetector"/>'s settings.</summary>
    public sealed record EndpointSettings
    {
        public int SampleRate { get; init; } = 16000;

        /// <summary>The silence after speech that ends the request.</summary>
        public int SilenceMs { get; init; } = 700;

        /// <summary>The speech that counts as speaking, not as a click or a breath.</summary>
        public int MinSpeechMs { get; init; } = 150;

        /// <summary>How long to wait for the first speech.</summary>
        public int NoSpeechMs { get; init; } = 8000;

        /// <summary>The longest request; it ends there.</summary>
        public int MaxRequestMs { get; init; } = 15000;

        /// <summary>The level (dBFS) under which audio never counts as speech.</summary>
        public double SpeechThresholdDb { get; init; } = -45;
    }

    /// <summary>
    /// Finds the end of a spoken request in 16-bit mono PCM by its level: a frame is speech when it is louder than both
    /// <see cref="EndpointSettings.SpeechThresholdDb"/> and the room's noise floor by <see cref="MarginDb"/>. Time is
    /// counted in samples, so the result doesn't depend on how fast audio arrives.
    /// </summary>
    public sealed class EndpointDetector
    {
        public const int FrameMs = 30;

        /// <summary>How far over the noise floor speech must be.</summary>
        public const double MarginDb = 9;

        // How far over the threshold the noise floor may push it: a request that starts with speech, before any pause
        // showed the floor, is still heard.
        private const double MaxRaiseDb = 15;

        // Digital silence (hassmic sends some ahead of each request) says nothing about the room.
        private const double SilenceDb = -100;

        // The floor follows a quieter room at once and a louder one slowly.
        private const double FloorRiseDbPerSecond = 1;

        private readonly EndpointSettings _settings;
        private readonly int _frameBytes;
        private readonly byte[] _partial;
        private int _partialLength;
        private int _elapsedMs;
        private int _speechRunMs;
        private int _silenceMs;
        private double? _floor;

        public EndpointDetector(EndpointSettings settings)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(settings.SampleRate, 1000);
            _settings = settings;
            _frameBytes = settings.SampleRate * FrameMs / 1000 * 2;
            _partial = new byte[_frameBytes];
        }

        /// <summary>Whether speech was heard.</summary>
        public bool HeardSpeech { get; private set; }

        /// <summary>The result so far; once it isn't <see cref="EndpointResult.Listening"/> it stays.</summary>
        public EndpointResult Result { get; private set; } = EndpointResult.Listening;

        /// <summary>Takes the next audio (16-bit little-endian mono PCM) and returns the result so far.</summary>
        public EndpointResult Process(ReadOnlySpan<byte> pcm)
        {
            while (Result == EndpointResult.Listening && pcm.Length > 0)
            {
                var take = Math.Min(_frameBytes - _partialLength, pcm.Length);
                pcm[..take].CopyTo(_partial.AsSpan(_partialLength));
                _partialLength += take;
                pcm = pcm[take..];
                if (_partialLength == _frameBytes)
                {
                    _partialLength = 0;
                    Result = Frame(LevelDb(_partial));
                }
            }
            return Result;
        }

        /// <summary>The RMS level of 16-bit little-endian PCM in dBFS; digital silence is <see cref="double.NegativeInfinity"/>.</summary>
        public static double LevelDb(ReadOnlySpan<byte> pcm)
        {
            var samples = pcm.Length / 2;
            if (samples == 0)
                return double.NegativeInfinity;
            double sum = 0;
            for (var i = 0; i < samples; i++)
            {
                double sample = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8);
                sum += sample * sample;
            }
            var rms = Math.Sqrt(sum / samples);
            return rms == 0 ? double.NegativeInfinity : 20 * Math.Log10(rms / 32768.0);
        }

        private EndpointResult Frame(double level)
        {
            _elapsedMs += FrameMs;
            if (level > SilenceDb)
                _floor = _floor is double floor && level >= floor ? floor + FloorRiseDbPerSecond * FrameMs / 1000 : level;

            var threshold = _settings.SpeechThresholdDb;
            if (_floor is double f)
                threshold = Math.Clamp(f + MarginDb, threshold, threshold + MaxRaiseDb);
            var speech = level >= threshold;

            if (!HeardSpeech)
            {
                _speechRunMs = speech ? _speechRunMs + FrameMs : Math.Max(0, _speechRunMs - FrameMs);
                if (_speechRunMs >= _settings.MinSpeechMs)
                    HeardSpeech = true;
                else if (_elapsedMs >= _settings.NoSpeechMs)
                    return EndpointResult.NoSpeech;
            }
            else
            {
                _silenceMs = speech ? 0 : _silenceMs + FrameMs;
                if (_silenceMs >= _settings.SilenceMs)
                    return EndpointResult.Ended;
            }

            if (_elapsedMs >= _settings.MaxRequestMs)
                return HeardSpeech ? EndpointResult.Ended : EndpointResult.NoSpeech;
            return EndpointResult.Listening;
        }
    }
}
