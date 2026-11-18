using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace RIoT2.Net.Devices.Services.EspHome
{
    /// <summary>
    /// The initiator's side of <c>Noise_NNpsk0_25519_ChaChaPoly_SHA256</c> with the ESPHome native API's prologue
    /// (Noise spec rev 34): <c>-&gt; psk, e</c> then <c>&lt;- e, ee</c>, both with empty payloads. A PSK handshake also
    /// mixes each ephemeral public key into the key.
    /// </summary>
    public sealed class NoiseHandshake
    {
        public const string ProtocolName = "Noise_NNpsk0_25519_ChaChaPoly_SHA256";
        public const int KeyLength = 32;
        public const int TagLength = 16;

        /// <summary>Message 1: the ephemeral public key and the tag of the empty payload.</summary>
        public const int Message1Length = KeyLength + TagLength;

        private static readonly byte[] Prologue = Encoding.ASCII.GetBytes("NoiseAPIInit\0\0");

        private readonly byte[] _psk;
        private readonly byte[] _ephemeral;
        private byte[] _h;
        private byte[] _ck;
        private byte[]? _k;
        private ulong _n;
        private int _step;

        /// <param name="psk">The 32-byte pre-shared key: the satellite's encryption key, or all zeros before it has one.</param>
        /// <param name="ephemeralPrivateKey">Tests fix the ephemeral key; otherwise it is random.</param>
        public NoiseHandshake(ReadOnlySpan<byte> psk, byte[]? ephemeralPrivateKey = null)
        {
            if (psk.Length != KeyLength)
                throw new ArgumentException("The pre-shared key must be 32 bytes.", nameof(psk));
            _psk = psk.ToArray();
            _ephemeral = ephemeralPrivateKey ?? RandomNumberGenerator.GetBytes(KeyLength);
            _h = SHA256.HashData(Encoding.ASCII.GetBytes(ProtocolName));
            _ck = (byte[])_h.Clone();
            MixHash(Prologue);
        }

        public byte[] WriteMessage1()
        {
            if (_step++ != 0)
                throw new InvalidOperationException("Message 1 was written already.");
            MixKeyAndHash(_psk);
            var e = new X25519PrivateKeyParameters(_ephemeral).GeneratePublicKey().GetEncoded();
            MixHash(e);
            MixKey(e);
            var message = new byte[Message1Length];
            e.CopyTo(message, 0);
            EncryptAndHash([]).CopyTo(message, KeyLength);
            return message;
        }

        /// <summary>Reads the responder's message 2 and splits: the initiator sends with the first key.</summary>
        /// <exception cref="EspHomeKeyException">Message 2 doesn't authenticate: the keys differ.</exception>
        public (NoiseCipher Send, NoiseCipher Receive) ReadMessage2(ReadOnlySpan<byte> message)
        {
            if (_step++ != 1)
                throw new InvalidOperationException("Message 2 comes after message 1.");
            if (message.Length != Message1Length)
                throw new EspHomeProtocolException($"The Noise handshake answer is {message.Length} bytes, not {Message1Length}.");
            var re = message[..KeyLength].ToArray();
            MixHash(re);
            MixKey(re);

            var shared = new byte[KeyLength];
            try
            {
                var agreement = new X25519Agreement();
                agreement.Init(new X25519PrivateKeyParameters(_ephemeral));
                agreement.CalculateAgreement(new X25519PublicKeyParameters(re), shared, 0);
            }
            catch (InvalidOperationException)
            {
                throw new EspHomeProtocolException("The satellite's ephemeral key is invalid.");
            }
            MixKey(shared);
            CryptographicOperations.ZeroMemory(shared);

            if (!DecryptAndHash(message[KeyLength..]))
                throw new EspHomeKeyException("The satellite's handshake answer doesn't authenticate: the encryption keys differ.");

            var (k1, k2) = Hkdf(_ck, []);
            CryptographicOperations.ZeroMemory(_ephemeral);
            CryptographicOperations.ZeroMemory(_psk);
            return (new NoiseCipher(k1), new NoiseCipher(k2));
        }

        private void MixHash(ReadOnlySpan<byte> data)
        {
            var buffer = new byte[_h.Length + data.Length];
            _h.CopyTo(buffer, 0);
            data.CopyTo(buffer.AsSpan(_h.Length));
            _h = SHA256.HashData(buffer);
        }

        private void MixKey(ReadOnlySpan<byte> input)
        {
            (_ck, _k) = Hkdf(_ck, input);
            _n = 0;
        }

        private void MixKeyAndHash(ReadOnlySpan<byte> input)
        {
            var (ck, temp, k) = Hkdf3(_ck, input);
            _ck = ck;
            MixHash(temp);
            _k = k;
            _n = 0;
        }

        private byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
        {
            var ciphertext = NoiseCipher.Seal(_k!, _n++, _h, plaintext);
            MixHash(ciphertext);
            return ciphertext;
        }

        private bool DecryptAndHash(ReadOnlySpan<byte> ciphertext)
        {
            if (NoiseCipher.Open(_k!, _n++, _h, ciphertext) == null)
                return false;
            MixHash(ciphertext);
            return true;
        }

        private static (byte[], byte[]) Hkdf(byte[] chainingKey, ReadOnlySpan<byte> input)
        {
            var temp = HMACSHA256.HashData(chainingKey, input);
            var output1 = HMACSHA256.HashData(temp, new byte[] { 1 });
            var output2 = HMACSHA256.HashData(temp, Append(output1, 2));
            CryptographicOperations.ZeroMemory(temp);
            return (output1, output2);
        }

        private static byte[] Append(byte[] data, byte last) => [.. data, last];

        private static (byte[], byte[], byte[]) Hkdf3(byte[] chainingKey, ReadOnlySpan<byte> input)
        {
            var temp = HMACSHA256.HashData(chainingKey, input);
            var output1 = HMACSHA256.HashData(temp, new byte[] { 1 });
            var output2 = HMACSHA256.HashData(temp, Append(output1, 2));
            var output3 = HMACSHA256.HashData(temp, Append(output2, 3));
            CryptographicOperations.ZeroMemory(temp);
            return (output1, output2, output3);
        }
    }

    /// <summary>
    /// One direction of a Noise transport: ChaCha20-Poly1305 with a counting nonce and no associated data. BouncyCastle's,
    /// as .NET has it only where the operating system does (not on every Windows).
    /// </summary>
    public sealed class NoiseCipher : IDisposable
    {
        private readonly byte[] _key;
        private ulong _n;

        public NoiseCipher(byte[] key)
        {
            _key = (byte[])key.Clone();
            CryptographicOperations.ZeroMemory(key);
        }

        /// <summary>The plaintext encrypted, with its tag after it.</summary>
        public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => Seal(_key, _n++, default, plaintext);

        /// <exception cref="EspHomeProtocolException">The frame doesn't authenticate.</exception>
        public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
        {
            if (ciphertext.Length < NoiseHandshake.TagLength)
                throw new EspHomeProtocolException("An encrypted frame is shorter than its tag.");
            var plaintext = Open(_key, _n, default, ciphertext)
                ?? throw new EspHomeProtocolException("An encrypted frame doesn't authenticate.");
            _n++;
            return plaintext;
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_key);

        internal static byte[] Seal(byte[] key, ulong n, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext) =>
            Process(true, key, n, ad, plaintext);

        internal static byte[]? Open(byte[] key, ulong n, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
        {
            if (ciphertext.Length < NoiseHandshake.TagLength)
                return null;
            try
            {
                return Process(false, key, n, ad, ciphertext);
            }
            catch (InvalidCipherTextException)
            {
                return null;
            }
        }

        private static byte[] Process(bool encrypt, byte[] key, ulong n, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> input)
        {
            var aead = new BcChaCha20Poly1305();
            aead.Init(encrypt, new AeadParameters(new KeyParameter(key), NoiseHandshake.TagLength * 8, Nonce(n), ad.ToArray()));
            var output = new byte[aead.GetOutputSize(input.Length)];
            var length = aead.ProcessBytes(input, output);
            length += aead.DoFinal(output.AsSpan(length));
            return length == output.Length ? output : output[..length];
        }

        // 32 zero bits, then the counter little-endian.
        private static byte[] Nonce(ulong n)
        {
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), n);
            return nonce;
        }
    }
}
