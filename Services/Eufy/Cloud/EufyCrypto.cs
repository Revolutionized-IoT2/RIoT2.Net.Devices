// Ported from eufy-sdk src/core/crypto.ts, src/transport/token-echo.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>A negotiated ECDH session: the shareKey and the X-Key-Ident the client minted for it.</summary>
    internal sealed record EufySessionEntry(string KeyIdent, string ShareKey, string ClientPublicKeyHex, string ClientPrivateKeyHex, DateTimeOffset CreatedAt);

    /// <summary>A key-exchange request in flight: the ephemeral key pair and the wrapped public key to send.</summary>
    internal sealed class EufyKeyExchangePrep : IDisposable
    {
        public required ECDiffieHellman Ecdh { get; init; }
        public required byte[] LocalKey { get; init; }
        public required string KeyIdent { get; init; }
        /// <summary>The base64 body to POST as <c>{ client_public_key }</c>.</summary>
        public required string EncryptedClientPublicKey { get; init; }
        /// <summary>The algo_ecdh headers for the key-exchange POST.</summary>
        public required IReadOnlyDictionary<string, string> Headers { get; init; }
        public required string ClientPublicKeyHex { get; init; }

        public void Dispose() => Ecdh.Dispose();
    }

    /// <summary>
    /// The eufy "mega" (Anker AIoT) cloud crypto, algo_ecdh: the per-host ECDH key exchange, the
    /// AES-128-CBC body encryption, the HMAC-SHA256 request signature and the login password encryption.
    /// </summary>
    internal static partial class EufyCrypto
    {
        /// <summary>The eufy_mega app localKey that bootstraps the key exchange on the *.eufy.com stack.</summary>
        public const string EufyMegaLocalKeyHex = "2500a7d5617812f9d52515b2c8f20a3d";

        /// <summary>The separate localKey of the <c>security-app-{shard}.eufylife.com</c> gateway.</summary>
        public const string EufylifeLocalKeyHex = "118c12c81e211149304bd70a0c071d01";

        /// <summary>The server's static P-256 public key (04||X||Y) the login password is encrypted against.</summary>
        public const string ServerStaticPublicKeyHex =
            "04c5c00c4f8d1197cc7c3167c52bf7acb054d722f0ef08dcd7e0883236e0d72a3868d9750cb47fa4619248f3d83f0f662671dadc6e2d31c2f41db0161651c7c076";

        /// <summary>32-hex random id, for X-Key-Ident / X-Request-Once.</summary>
        public static string GenId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        /// <summary>Unix seconds as a string (X-Request-Ts).</summary>
        public static string NowSec(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

#pragma warning disable CA5351 // md5 is the protocol's own derivation (gtoken, openudid seed, image key), not a security choice.
        public static string Md5Hex(string input) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));
#pragma warning restore CA5351

        /// <summary>The gtoken header: md5(user_id) hex.</summary>
        public static string Gtoken(string userId) => Md5Hex(userId);

        /// <summary>The 16-byte AES key: the first half of the shared key.</summary>
        public static byte[] AesKey(string shareKeyHex) => Convert.FromHexString(shareKeyHex.AsSpan(0, 32));

        /// <summary>The HMAC key: the first 32 hex chars of the shared key, as UTF-8.</summary>
        public static byte[] SignKey(string shareKeyHex) => Encoding.UTF8.GetBytes(shareKeyHex.Length > 32 ? shareKeyHex[..32] : shareKeyHex);

        /// <summary>base64( IV(16) || AES-128-CBC-PKCS7(plaintext) ).</summary>
        public static string EncryptBody(string plaintext, string shareKeyHex, byte[]? iv = null) =>
            EncryptBody(Encoding.UTF8.GetBytes(plaintext), shareKeyHex, iv);

        public static string EncryptBody(byte[] plaintext, string shareKeyHex, byte[]? iv = null) =>
            Convert.ToBase64String(WrapCbc(AesKey(shareKeyHex), plaintext, iv));

        /// <summary>Decrypts a base64( IV(16) || AES-128-CBC-PKCS7 ) body.</summary>
        public static byte[] DecryptBody(string b64, string shareKeyHex) => UnwrapCbc(AesKey(shareKeyHex), Convert.FromBase64String(b64));

        /// <summary>x-signature = HMAC-SHA256(signKey, [ts, once, encBody?].join("+")) hex; the body is omitted when empty.</summary>
        public static string SignRequest(string shareKeyHex, string ts, string once, string? encBodyB64 = null)
        {
            var text = string.IsNullOrEmpty(encBodyB64) ? $"{ts}+{once}" : $"{ts}+{once}+{encBodyB64}";
            return Convert.ToHexStringLower(HMACSHA256.HashData(SignKey(shareKeyHex), Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>The headers that mark <paramref name="encBody"/> as algo_ecdh-encrypted under <paramref name="entry"/> and sign it.</summary>
        public static Dictionary<string, string> SignedHeaders(EufySessionEntry entry, string encBody, DateTimeOffset now)
        {
            var ts = NowSec(now);
            var once = GenId();
            return new Dictionary<string, string>
            {
                ["x-encryption-info"] = "algo_ecdh",
                ["x-key-ident"] = entry.KeyIdent,
                ["x-request-ts"] = ts,
                ["x-request-once"] = once,
                ["x-signature"] = SignRequest(entry.ShareKey, ts, once, encBody),
            };
        }

        /// <summary>Builds a key-exchange request: an ephemeral P-256 key pair whose public hex is AES-128-CBC(localKey)-wrapped and signed.</summary>
        public static EufyKeyExchangePrep PrepareKeyExchange(DateTimeOffset now, string localKeyHex = EufyMegaLocalKeyHex)
        {
            var localKey = Convert.FromHexString(localKeyHex);
            var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var clientPublicKeyHex = PublicKeyHex(ecdh);
            var encrypted = Convert.ToBase64String(WrapCbc(localKey, Encoding.UTF8.GetBytes(clientPublicKeyHex)));
            var ts = NowSec(now);
            var once = GenId();
            var keyIdent = GenId();
            var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(localKeyHex), Encoding.UTF8.GetBytes($"{ts}+{once}+{encrypted}")));
            return new EufyKeyExchangePrep
            {
                Ecdh = ecdh,
                LocalKey = localKey,
                KeyIdent = keyIdent,
                EncryptedClientPublicKey = encrypted,
                ClientPublicKeyHex = clientPublicKeyHex,
                Headers = new Dictionary<string, string>
                {
                    ["x-encryption-info"] = "algo_ecdh",
                    ["x-replay-info"] = "replay",
                    ["x-key-ident"] = keyIdent,
                    ["x-request-ts"] = ts,
                    ["x-request-once"] = once,
                    ["x-signature"] = sig,
                },
            };
        }

        /// <summary>Unwraps the server's public key with the localKey, derives the ECDH secret and takes its first 32 hex chars as the shareKey.</summary>
        public static EufySessionEntry FinishKeyExchange(EufyKeyExchangePrep prep, string serverPublicKeyB64, DateTimeOffset now)
        {
            var serverPubHex = Encoding.UTF8.GetString(UnwrapCbc(prep.LocalKey, Convert.FromBase64String(serverPublicKeyB64)));
            var shared = DeriveSecret(prep.Ecdh, serverPubHex);
            var shareKey = Convert.ToHexStringLower(shared).PadLeft(64, '0')[..32];
            var d = prep.Ecdh.ExportParameters(true).D ?? [];
            return new EufySessionEntry(prep.KeyIdent, shareKey, prep.ClientPublicKeyHex, Convert.ToHexStringLower(d), now);
        }

        /// <summary>
        /// Encrypts the login password against the server's static key: AES-256-CBC with the whole 32-byte ECDH
        /// secret as key and its first 16 bytes as IV. Returns the client public key hex and the base64 ciphertext.
        /// </summary>
        public static (string ClientPublicKeyHex, string EncryptedPassword) EncryptLoginPassword(string password, string serverPublicKeyHex = ServerStaticPublicKeyHex)
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var shared = DeriveSecret(ecdh, serverPublicKeyHex);
            using var aes = Aes.Create();
            aes.Key = shared;
            var ct = aes.EncryptCbc(Encoding.UTF8.GetBytes(password), shared.AsSpan(0, 16), PaddingMode.PKCS7);
            return (PublicKeyHex(ecdh), Convert.ToBase64String(ct));
        }

        /// <summary>The uncompressed public key, <c>04||X||Y</c>, as lowercase hex.</summary>
        public static string PublicKeyHex(ECDiffieHellman ecdh)
        {
            var q = ecdh.ExportParameters(false).Q;
            return "04" + Convert.ToHexStringLower(q.X!) + Convert.ToHexStringLower(q.Y!);
        }

        /// <summary>A P-256 public key from its uncompressed <c>04||X||Y</c> hex.</summary>
        public static ECDiffieHellman ImportPublicKey(string uncompressedHex)
        {
            var raw = Convert.FromHexString(uncompressedHex.Trim());
            if (raw.Length != 65 || raw[0] != 0x04)
                throw new CryptographicException("not an uncompressed P-256 public key");
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
            });
        }

        /// <summary>The raw ECDH secret (the shared point's X coordinate, 32 bytes), as Node's <c>computeSecret</c> returns it.</summary>
        public static byte[] DeriveSecret(ECDiffieHellman own, string peerPublicKeyHex)
        {
            using var peer = ImportPublicKey(peerPublicKeyHex);
            return own.DeriveRawSecretAgreement(peer.PublicKey);
        }

        /// <summary>IV(16) || AES-CBC-PKCS7(plaintext); a random IV unless one is given.</summary>
        public static byte[] WrapCbc(byte[] key, byte[] plaintext, byte[]? iv = null)
        {
            iv ??= RandomNumberGenerator.GetBytes(16);
            using var aes = Aes.Create();
            aes.Key = key;
            var ct = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
            var output = new byte[16 + ct.Length];
            iv.CopyTo(output, 0);
            ct.CopyTo(output, 16);
            return output;
        }

        /// <summary>Decrypts IV(16) || AES-CBC-PKCS7(ciphertext).</summary>
        public static byte[] UnwrapCbc(byte[] key, byte[] raw)
        {
            if (raw.Length < 16)
                throw new CryptographicException("ciphertext shorter than its IV");
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.DecryptCbc(raw.AsSpan(16), raw.AsSpan(0, 16), PaddingMode.PKCS7);
        }

        /// <summary>Strips a token the gateway echoed back into its own error message.</summary>
        public static string WithoutTokenEcho(string text) => TokenEcho().Replace(text, "token = <redacted>");

        [GeneratedRegex("token\\s*[=:]\\s*\"?[A-Za-z0-9._-]{8,}\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex TokenEcho();
    }
}
