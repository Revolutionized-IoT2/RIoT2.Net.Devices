// Ported from eufy-sdk src/transport/http/decodeImageV1.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>
    /// The v1 <c>eufysecurity:</c> push-thumbnail decoder, and <see cref="NormalizePushImage"/>, which routes a
    /// downloaded image through the v1 (device key) or v2 (keyless) decoder.
    /// </summary>
    /// <remarks>
    /// Wire format: <c>eufysecurity:&lt;SERIAL(16)&gt;:&lt;CODE(10)&gt;:&lt;DATA&gt;</c>. The first 256 bytes of DATA are
    /// AES-128-ECB encrypted with a key derived from the camera serial, the station <c>p2p_did</c> and the code.
    /// </remarks>
    internal static partial class PushImageDecoder
    {
        public const string V1Prefix = "eufysecurity";

        /// <summary>The digit-mixing suffix of the station <c>p2p_did</c>; NaN where JavaScript's parse would give NaN.</summary>
        public static double GetIdSuffix(string p2pDid)
        {
            var match = P2pDid().Match(p2pDid);
            if (!match.Success)
                return 0;
            var digits = match.Groups[1].Value;
            double Digit(int index) => index < digits.Length ? digits[index] - '0' : double.NaN;
            var num1 = Digit(0);
            var num2 = Digit(1);
            var num3 = Digit(3);
            var num4 = Digit(5);
            var result = num1 + num2 + num3;
            if (num3 < 5)
                result += num3;
            return result + num4;
        }

        /// <summary>The image "base code": a tail of the camera serial plus the p2p_did suffix.</summary>
        public static string GetImageBaseCode(string serialNumber, string p2pDid)
        {
            var nr = serialNumber.Length > 0 && Uri.IsHexDigit(serialNumber[^1]) ? Convert.ToInt32(serialNumber[^1].ToString(), 16) : 0;
            nr = (nr + 10) % 10;
            var baseCode = nr < serialNumber.Length ? serialNumber[nr..] : "";
            return baseCode + JsNumber(GetIdSuffix(p2pDid));
        }

        /// <summary>The per-image seed: md5 of the p2p_did suffix complement and the image's own code, uppercase.</summary>
        public static string GetImageSeed(string p2pDid, string code)
        {
            var nCode = JsParseInt(code.Length > 2 ? code[2..] : "");
            var prefix = 1000 - GetIdSuffix(p2pDid);
            return EufyCrypto.Md5Hex(JsNumber(prefix) + JsNumber(nCode)).ToUpperInvariant();
        }

        /// <summary>The AES-128-ECB key of a v1 thumbnail (hex, uppercase; its first 16 chars are the key bytes).</summary>
        public static string GetImageKey(string serialNumber, string p2pDid, string code)
        {
            var baseCode = GetImageBaseCode(serialNumber, p2pDid);
            var seed = GetImageSeed(p2pDid, code);
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"01{baseCode}{seed}")).Select(b => (int)b).ToArray();
            var startByte = hashBytes[10];
            for (var i = 0; i < 32; i++)
            {
                var b = hashBytes[i];
                var fixedByte = startByte;
                if (i < 31)
                    fixedByte = hashBytes[i + 1];
                if (i == 31 || (i & 1) != 0)
                {
                    hashBytes[10] = fixedByte;
                    if (126 < b || 126 < hashBytes[10])
                    {
                        if (b < hashBytes[10] || b - hashBytes[10] == 0)
                            hashBytes[i] = hashBytes[10] - b;
                        else
                            hashBytes[i] = b - hashBytes[10];
                    }
                }
                else if (b < 125 || fixedByte < 125)
                {
                    hashBytes[i] = fixedByte + b;
                }
            }
            return Convert.ToHexString(hashBytes[16..].Select(v => (byte)(v & 0xff)).ToArray());
        }

        /// <summary>True for a legacy v1 <c>eufysecurity:</c> blob (not the v2 variant).</summary>
        public static bool IsV1Image(byte[] data) =>
            data.Length >= 41 && Encoding.Latin1.GetString(data, 0, 12) == V1Prefix && Encoding.Latin1.GetString(data, 0, 3) != "v2_";

        /// <summary>Decodes a v1 blob to a plain JPEG with the station's p2p_did; null when it isn't v1.</summary>
        public static byte[]? DecodeImageV1(byte[] data, string p2pDid)
        {
            if (!IsV1Image(data))
                return null;
            var serialNumber = Encoding.Latin1.GetString(data, 13, 16);
            var code = Encoding.Latin1.GetString(data, 30, 10);
            var otherData = data[41..];
            var encryptedLength = Math.Min(256, otherData.Length);
            var imageKey = GetImageKey(serialNumber, p2pDid, code);
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(imageKey)[..16];
#pragma warning disable CA5358 // ECB is the vendor's wire format for the v1 thumbnail head.
            var decrypted = aes.DecryptEcb(otherData.AsSpan(0, encryptedLength), PaddingMode.None);
#pragma warning restore CA5358
            decrypted.CopyTo(otherData, 0);
            return otherData;
        }

        /// <summary>Decodes a recognised v1 (device key) or v2 (keyless) wrapper; other media is returned unchanged.</summary>
        public static byte[] NormalizePushImage(byte[] data, string? p2pDid = null)
        {
            if (PushImageDecoderV2.IsV2Image(data))
                return PushImageDecoderV2.DecodeImageV2(data) ?? data;
            if (string.IsNullOrEmpty(p2pDid) || !IsV1Image(data))
                return data;
            return DecodeImageV1(data, p2pDid) ?? data;
        }

        /// <summary>JavaScript's <c>Number.parseInt(s)</c> for base 10: the leading integer, or NaN.</summary>
        internal static double JsParseInt(string s)
        {
            var text = s.TrimStart();
            var i = 0;
            var negative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                negative = text[i] == '-';
                i++;
            }
            var startDigits = i;
            double value = 0;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            {
                value = value * 10 + (text[i] - '0');
                i++;
            }
            if (i == startDigits)
                return double.NaN;
            return negative ? -value : value;
        }

        /// <summary>A number as JavaScript prints it in a template literal (integers here; NaN as "NaN").</summary>
        internal static string JsNumber(double value) =>
            double.IsNaN(value) ? "NaN" : value.ToString("R", CultureInfo.InvariantCulture);

        [GeneratedRegex("^[A-Z]+-([0-9]+)-[A-Z]+\\z", RegexOptions.CultureInvariant)]
        private static partial Regex P2pDid();
    }
}
