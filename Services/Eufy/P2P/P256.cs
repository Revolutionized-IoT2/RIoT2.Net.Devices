// Ported from eufy-sdk src/transport/p2p/codec.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// Node's createECDH("prime256v1") accepts a bare private scalar and compressed points; .NET does not
// expose either directly, so the curve arithmetic the ECIES envelope needs is done here.

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>The NIST P-256 arithmetic of the eufy ECIES envelope: scalar multiplication, point (de)compression and the ECDH X coordinate.</summary>
    internal static class P256
    {
        private static readonly BigInteger P = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
        private static readonly BigInteger N = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
        private static readonly BigInteger B = Hex("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
        private static readonly BigInteger Gx = Hex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296");
        private static readonly BigInteger Gy = Hex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5");

        private static BigInteger Hex(string hex) => BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        private static BigInteger Mod(BigInteger a)
        {
            var r = a % P;
            return r.Sign < 0 ? r + P : r;
        }

        private static BigInteger Inverse(BigInteger a) => BigInteger.ModPow(Mod(a), P - 2, P);

        private static BigInteger FromBytes(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

        internal static byte[] ToBytes32(BigInteger v)
        {
            var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length == 32) return raw;
            var out32 = new byte[32];
            if (raw.Length > 32) Array.Copy(raw, raw.Length - 32, out32, 0, 32);
            else Array.Copy(raw, 0, out32, 32 - raw.Length, raw.Length);
            return out32;
        }

        private readonly record struct Jacobian(BigInteger X, BigInteger Y, BigInteger Z)
        {
            public bool IsInfinity => Z.IsZero;
        }

        private static readonly Jacobian Infinity = new(0, 1, 0);

        private static Jacobian Double(Jacobian p)
        {
            if (p.IsInfinity || p.Y.IsZero) return Infinity;
            var ysq = Mod(p.Y * p.Y);
            var s = Mod(4 * p.X * ysq);
            var zsq = Mod(p.Z * p.Z);
            var m = Mod(3 * (p.X - zsq) * (p.X + zsq)); // a = -3
            var nx = Mod(m * m - 2 * s);
            var ny = Mod(m * (s - nx) - 8 * ysq * ysq);
            var nz = Mod(2 * p.Y * p.Z);
            return new Jacobian(nx, ny, nz);
        }

        private static Jacobian Add(Jacobian p, Jacobian q)
        {
            if (p.IsInfinity) return q;
            if (q.IsInfinity) return p;
            var z1z1 = Mod(p.Z * p.Z);
            var z2z2 = Mod(q.Z * q.Z);
            var u1 = Mod(p.X * z2z2);
            var u2 = Mod(q.X * z1z1);
            var s1 = Mod(p.Y * q.Z * z2z2);
            var s2 = Mod(q.Y * p.Z * z1z1);
            if (u1 == u2) return s1 == s2 ? Double(p) : Infinity;
            var h = Mod(u2 - u1);
            var r = Mod(s2 - s1);
            var hh = Mod(h * h);
            var hhh = Mod(h * hh);
            var v = Mod(u1 * hh);
            var nx = Mod(r * r - hhh - 2 * v);
            var ny = Mod(r * (v - nx) - s1 * hhh);
            var nz = Mod(p.Z * q.Z * h);
            return new Jacobian(nx, ny, nz);
        }

        private static (BigInteger X, BigInteger Y)? Multiply(BigInteger k, BigInteger x, BigInteger y)
        {
            var result = Infinity;
            var addend = new Jacobian(x, y, 1);
            while (k.Sign > 0)
            {
                if (!k.IsEven) result = Add(result, addend);
                addend = Double(addend);
                k >>= 1;
            }
            if (result.IsInfinity) return null;
            var zi = Inverse(result.Z);
            var zi2 = Mod(zi * zi);
            return (Mod(result.X * zi2), Mod(result.Y * zi2 * zi));
        }

        private static bool OnCurve(BigInteger x, BigInteger y) =>
            x < P && y < P && Mod(y * y) == Mod(x * x * x - 3 * x + B);

        /// <summary>Parses a compressed (33-byte) or uncompressed (65-byte) SEC1 point; null when it is not on the curve.</summary>
        internal static (BigInteger X, BigInteger Y)? ParsePoint(ReadOnlySpan<byte> point)
        {
            if (point.Length == 65 && point[0] == 0x04)
            {
                var x = FromBytes(point.Slice(1, 32));
                var y = FromBytes(point.Slice(33, 32));
                return OnCurve(x, y) ? (x, y) : null;
            }
            if (point.Length == 33 && (point[0] == 0x02 || point[0] == 0x03))
            {
                var x = FromBytes(point.Slice(1, 32));
                if (x >= P) return null;
                var rhs = Mod(x * x * x - 3 * x + B);
                var y = BigInteger.ModPow(rhs, (P + 1) / 4, P);
                if (Mod(y * y) != rhs) return null;
                if ((y.IsEven ? 0x02 : 0x03) != point[0]) y = P - y;
                return (x, y);
            }
            return null;
        }

        private static BigInteger Scalar(ReadOnlySpan<byte> privateKey)
        {
            var d = FromBytes(privateKey);
            if (d.IsZero || d >= N) throw new ArgumentException("invalid P-256 private key", nameof(privateKey));
            return d;
        }

        private static (BigInteger X, BigInteger Y) PublicPoint(ReadOnlySpan<byte> privateKey) =>
            Multiply(Scalar(privateKey), Gx, Gy) ?? throw new ArgumentException("invalid P-256 private key", nameof(privateKey));

        /// <summary>The compressed (33-byte) public key of a 32-byte private scalar.</summary>
        internal static byte[] CompressedPublicKey(ReadOnlySpan<byte> privateKey)
        {
            var q = PublicPoint(privateKey);
            var out33 = new byte[33];
            out33[0] = q.Y.IsEven ? (byte)0x02 : (byte)0x03;
            ToBytes32(q.X).CopyTo(out33, 1);
            return out33;
        }

        /// <summary>The uncompressed (65-byte) public key of a 32-byte private scalar.</summary>
        internal static byte[] UncompressedPublicKey(ReadOnlySpan<byte> privateKey)
        {
            var q = PublicPoint(privateKey);
            var out65 = new byte[65];
            out65[0] = 0x04;
            ToBytes32(q.X).CopyTo(out65, 1);
            ToBytes32(q.Y).CopyTo(out65, 33);
            return out65;
        }

        /// <summary>ECDH: the 32-byte X coordinate of <c>d·peer</c> (Node's <c>computeSecret</c>).</summary>
        internal static byte[] SharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPoint)
        {
            var peer = ParsePoint(peerPoint) ?? throw new ArgumentException("peer key is not a P-256 point", nameof(peerPoint));
            var s = Multiply(Scalar(privateKey), peer.X, peer.Y) ?? throw new ArgumentException("degenerate shared point", nameof(peerPoint));
            return ToBytes32(s.X);
        }

        /// <summary>A fresh random private scalar.</summary>
        internal static byte[] NewPrivateKey()
        {
            var buf = new byte[32];
            while (true)
            {
                RandomNumberGenerator.Fill(buf);
                var d = FromBytes(buf);
                if (!d.IsZero && d < N) return buf;
            }
        }
    }
}
