// Ported from eufy-sdk src/transport/ffmpeg.ts and the Annex-B → JPEG decode of src/transport/p2p/media.ts
// (annexbToJpeg, jpegGeometry) @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>A collected burst that ffmpeg could not turn into an image (a property of the burst, not of ffmpeg).</summary>
    internal sealed class SnapshotDecodeException : EufyP2PException
    {
        public SnapshotDecodeException(string message) : base(message) { }
    }

    /// <summary>The one place that shells out to ffmpeg.</summary>
    internal static class Ffmpeg
    {
        /// <summary>ffmpeg's own verbosity (<c>-loglevel</c>); stderr goes to the logger at debug.</summary>
        public const string LogLevel = "error";

        private static readonly HashSet<int> JpegStandaloneMarkers = [0x01, 0xd8, 0xd9, 0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7];
        private static readonly HashSet<int> JpegNotFrameHeaders = [0xc4, 0xc8, 0xcc];

        /// <summary>Which binary runs: a blank value means the bare name <c>ffmpeg</c>, looked up on PATH; any other value is used exactly as given.</summary>
        public static string Executable(string? path) => string.IsNullOrWhiteSpace(path) ? "ffmpeg" : path;

        /// <summary>The ffmpeg arguments that decode one Annex-B access unit (starting at a keyframe) to a single JPEG on stdout.</summary>
        public static IReadOnlyList<string> SnapshotArguments(VideoCodec codec) =>
        [
            "-hide_banner", "-loglevel", LogLevel,
            "-f", codec == VideoCodec.H265 ? "hevc" : "h264",
            "-i", "pipe:0",
            "-frames:v", "1",
            "-pix_fmt", "yuvj420p",
            "-f", "image2",
            "-vcodec", "mjpeg",
            "pipe:1",
        ];

        /// <summary>The full path the executable resolves to (an explicit path, or a PATH lookup honouring PATHEXT), or null.</summary>
        public static string? Locate(string executable)
        {
            var hasDirectory = executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(executable);
            var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(executable)
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("").ToArray()
                : [""];
            IEnumerable<string> candidates = hasDirectory
                ? extensions.Select(ext => executable + ext)
                : (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(dir => extensions.Select(ext => Path.Combine(dir.Trim('"'), executable + ext)));
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    // not a usable path
                }
            }
            return null;
        }

        /// <summary>The <see cref="InvalidOperationException"/> a missing ffmpeg is reported with.</summary>
        public static InvalidOperationException NotFound(string executable, Exception? inner = null) =>
            new($"ffmpeg not found at '{executable}'; install ffmpeg or set the account's FfmpegPath to its executable.", inner);

        /// <summary>
        /// The width and height of a JPEG's first frame header (SOFn), or null when it declares none. The snapshot's
        /// geometry is read back from the image the encoder produced, never from the stream header.
        /// </summary>
        public static (int Width, int Height)? JpegGeometry(byte[] jpeg)
        {
            var at = 2;
            while (at + 1 < jpeg.Length && jpeg[at] == 0xff)
            {
                var marker = jpeg[at + 1];
                if (marker == 0xff)
                {
                    at++;
                    continue;
                }
                if (marker >= 0xc0 && marker <= 0xcf && !JpegNotFrameHeaders.Contains(marker))
                {
                    if (at + 9 > jpeg.Length) return null;
                    return ((jpeg[at + 7] << 8) | jpeg[at + 8], (jpeg[at + 5] << 8) | jpeg[at + 6]);
                }
                if (JpegStandaloneMarkers.Contains(marker)) at += 2;
                else if (at + 4 <= jpeg.Length) at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
                else return null;
            }
            return null;
        }

        /// <summary>
        /// Decodes an Annex-B buffer (H.264 or H.265, starting at a keyframe) to a single JPEG. Throws
        /// <see cref="InvalidOperationException"/> when ffmpeg cannot be started and <see cref="SnapshotDecodeException"/>
        /// when the burst yields no image. The process is killed on cancellation.
        /// </summary>
        public static async Task<byte[]> AnnexBToJpegAsync(byte[] annexb, string executable, ILogger logger, CancellationToken cancellationToken)
        {
            var codec = AnnexB.SniffCodec(annexb) == VideoCodec.H265 ? VideoCodec.H265 : VideoCodec.H264;
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in SnapshotArguments(codec)) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start()) throw NotFound(executable);
            }
            catch (Win32Exception e)
            {
                throw NotFound(executable, e);
            }

            try
            {
                var stdout = new MemoryStream();
                var readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
                var readErr = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(annexb, cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // ffmpeg closed its input early; its exit code and stderr tell why
                }
                finally
                {
                    try { process.StandardInput.Close(); }
                    catch (IOException) { }
                }
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await readOut.ConfigureAwait(false);
                var stderr = await readErr.ConfigureAwait(false);
                var text = stderr.TrimEnd();
                if (text.Length > 0) logger.LogDebug("[ffmpeg] {Text}", text);

                var jpeg = stdout.ToArray();
                var encoded = jpeg.Length >= 3 && jpeg[0] == 0xff && jpeg[1] == 0xd8 && jpeg[2] == 0xff;
                if (encoded && JpegGeometry(jpeg) is not null) return jpeg;
                var diagnostics = text.Length > 200 ? text[..200] : text;
                var what = process.ExitCode != 0
                    ? $"ffmpeg exited {process.ExitCode}"
                    : encoded ? "encoded image declares no frame header" : "no complete frame in the burst";
                throw new SnapshotDecodeException(diagnostics.Length > 0 ? $"{what}: {diagnostics}" : what);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
        }
    }
}
