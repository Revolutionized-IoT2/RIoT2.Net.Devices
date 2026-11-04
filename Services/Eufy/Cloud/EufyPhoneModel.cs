// Ported from eufy-sdk src/transport/http/phone-model.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Globalization;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>
    /// A realistic phone model and media user agent for this install's device identity, seeded by the
    /// install's openudid so it is stable across runs (a changing identity looks like a new device and asks for 2FA).
    /// </summary>
    internal static class EufyPhoneModel
    {
        /// <summary>Brand → the parts a model string is composed from: <c>{brand}{first}{second}</c>, in upstream key order.</summary>
        private static readonly (string Brand, string[] First, string[] Second)[] PhoneModels =
        [
            ("Pixel ", ["6", "6a", "6 Pro", "7", "7a", "7 Pro", "8", "8a", "8 Pro", "9", "9 Pro"], [""]),
            ("SM-", ["G991B", "G996B", "G998B", "S901B", "S906B", "S908B", "S911B", "S918B", "A525F", "A536B", "A546B", "N986B"], [""]),
            ("Redmi Note ", ["10", "11", "12", "13"], ["", " Pro", " Pro+", " 5G"]),
            ("OnePlus ", ["9", "10", "11", "12"], ["", " Pro", "R"]),
            ("CPH", ["2001", "2005", "2211", "2247", "2451", "2609"], [""]),
            ("V", ["2050", "2109", "2207", "2312"], [""]),
            ("M", ["2101", "2103", "2201", "2210", "2304", "2312"], ["K7AG", "K6P", "J20C", "DRG4C"]),
        ];

        private static readonly string[] AndroidVersions = ["12", "13", "14", "15"];
        private static readonly string[] BuildPrefixes = ["TP1A", "TQ3A", "UP1A", "UQ1A", "AP1A", "AP2A"];

        /// <summary>A realistic Android phone model, stable for a given seed.</summary>
        public static string RandomPhoneModel(string? seed = null)
        {
            var rnd = Rng(seed);
            var (brand, first, second) = Pick(PhoneModels, rnd);
            return $"{brand}{Pick(first, rnd)}{Pick(second, rnd)}".Trim();
        }

        /// <summary>A realistic Dalvik user agent consistent with <paramref name="model"/>, stable for a given seed.</summary>
        public static string RandomUserAgent(string? seed = null, string? model = null)
        {
            var rnd = Rng(seed);
            var device = model ?? RandomPhoneModel(seed);
            var prefix = Pick(BuildPrefixes, rnd);
            var middle = (220000 + (long)Math.Floor(rnd() * 79999)).ToString(CultureInfo.InvariantCulture);
            if (middle.Length > 6)
                middle = middle[..6];
            var last = ((long)Math.Floor(rnd() * 900) + 100).ToString(CultureInfo.InvariantCulture);
            return $"Dalvik/2.1.0 (Linux; U; Android {Pick(AndroidVersions, rnd)}; {device} Build/{prefix}.{middle}.{last})";
        }

        private static T Pick<T>(T[] items, Func<double> rnd) => items[(int)Math.Floor(rnd() * items.Length)];

        private static Func<double> Rng(string? seed) => seed is null ? Random.Shared.NextDouble : Mulberry32(HashSeed(seed));

        /// <summary>FNV-1a over the UTF-16 code units of the seed.</summary>
        internal static uint HashSeed(string seed)
        {
            var h = 2166136261u;
            foreach (var c in seed)
            {
                h ^= c;
                h = unchecked(h * 16777619u);
            }
            return h;
        }

        /// <summary>The mulberry32 PRNG, bit-for-bit as the JavaScript original.</summary>
        internal static Func<double> Mulberry32(uint seed)
        {
            var s = unchecked((int)seed);
            return () =>
            {
                unchecked
                {
                    s += 0x6d2b79f5;
                    var t = (s ^ (int)((uint)s >> 15)) * (1 | s);
                    t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
                    return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
                }
            };
        }
    }
}
