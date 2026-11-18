using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Net.Devices.Services.Voice
{
    /// <summary>16-bit little-endian PCM audio.</summary>
    public sealed record PcmAudio(int Rate, int Channels, byte[] Data)
    {
        public TimeSpan Duration => Rate > 0 && Channels > 0 ? TimeSpan.FromSeconds(Data.Length / (2.0 * Channels * Rate)) : TimeSpan.Zero;
    }

    /// <summary>WAV files of 16-bit PCM.</summary>
    public static class Wav
    {
        /// <summary>A WAV file of <paramref name="audio"/>.</summary>
        public static byte[] Encode(PcmAudio audio)
        {
            var file = new byte[44 + audio.Data.Length];
            var span = file.AsSpan();
            Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + audio.Data.Length);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
            BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
            BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)audio.Channels);
            BinaryPrimitives.WriteInt32LittleEndian(span[24..], audio.Rate);
            BinaryPrimitives.WriteInt32LittleEndian(span[28..], audio.Rate * audio.Channels * 2);
            BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(audio.Channels * 2));
            BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
            Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[40..], audio.Data.Length);
            audio.Data.CopyTo(span[44..]);
            return file;
        }

        /// <summary>
        /// The PCM of a WAV file of 16-bit PCM. A streamed file's unknown data length (0 or 0xFFFFFFFF) means up to the
        /// file's end.
        /// </summary>
        public static PcmAudio Decode(ReadOnlySpan<byte> file)
        {
            if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WAVE"u8))
                throw new FormatException("The audio is not a WAV file.");
            int? rate = null, channels = null;
            var at = 12;
            while (at + 8 <= file.Length)
            {
                var id = file.Slice(at, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
                var body = at + 8;
                if (id.SequenceEqual("fmt "u8))
                {
                    if (size < 16 || body + 16 > file.Length)
                        throw new FormatException("The WAV file's format is cut short.");
                    var format = BinaryPrimitives.ReadUInt16LittleEndian(file[body..]);
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(file[(body + 14)..]);
                    if (format is not (1 or 0xFFFE) || bits != 16)
                        throw new FormatException($"The WAV file is not 16-bit PCM (format {format}, {bits} bits).");
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(file[(body + 2)..]);
                    rate = BinaryPrimitives.ReadInt32LittleEndian(file[(body + 4)..]);
                }
                else if (id.SequenceEqual("data"u8))
                {
                    if (rate is not > 0 || channels is not > 0)
                        throw new FormatException("The WAV file has no format before its data.");
                    var rest = file.Length - body;
                    var length = size == 0 || size > rest ? rest : (int)size;
                    length -= length % (2 * channels.Value);
                    return new PcmAudio(rate.Value, channels.Value, file.Slice(body, length).ToArray());
                }
                if (size > (uint)(file.Length - body))
                    break;
                at = body + (int)size + (int)(size & 1);
            }
            throw new FormatException("The WAV file has no audio data.");
        }
    }
}
