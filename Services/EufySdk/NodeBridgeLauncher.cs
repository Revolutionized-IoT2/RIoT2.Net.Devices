using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Services.EufySdk
{
    /// <summary>One running bridge: the lines it writes to stdout, and its stdin.</summary>
    internal interface IEufyBridgeConnection : IAsyncDisposable
    {
        /// <summary>The protocol lines the bridge wrote, in order; completes when the bridge exits.</summary>
        ChannelReader<string> Output { get; }
        /// <summary>Completes with the exit code when the bridge exits.</summary>
        Task<int> Exited { get; }
        Task WriteLineAsync(string line, CancellationToken cancellationToken);
    }

    /// <summary>Starts bridges; tests replace it.</summary>
    internal interface IEufyBridgeLauncher
    {
        Task<IEufyBridgeConnection> StartAsync(string dataDirectory, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Runs the embedded bridge bundle (<c>EufySdkBridge/</c>, built into <c>eufy-sdk-bridge.mjs</c>) with Node.js. The
    /// bundle is written to <c>&lt;data&gt;/eufy-sdk/</c>, because the plugin folder may be read-only.
    /// </summary>
    internal sealed class NodeBridgeLauncher : IEufyBridgeLauncher
    {
        public const string BundleResource = "RIoT2.Net.Devices.EufySdk.eufy-sdk-bridge.mjs";
        private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

        private readonly ILogger _logger;
        private readonly string _nodePath;

        public NodeBridgeLauncher(ILogger logger, string nodePath = "node")
        {
            _logger = logger;
            _nodePath = nodePath;
        }

        public async Task<IEufyBridgeConnection> StartAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            var script = await ExtractBundleAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(_nodePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            start.ArgumentList.Add(script);
            if (_logger.IsEnabled(LogLevel.Debug))
                start.Environment["EUFY_BRIDGE_LOG"] = "debug";

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                process.Start();
            }
            catch (Win32Exception error)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"Node.js was not found at '{_nodePath}'. The eufy-sdk device needs Node.js 24.5 or newer on the PATH (the RIoT2 Node images include it).", error);
            }

            return new NodeBridgeConnection(process, _logger);
        }

        /// <summary>Writes the bundle to <c>&lt;data&gt;/eufy-sdk/bridge-&lt;hash&gt;.mjs</c> unless it is there, and removes older ones.</summary>
        internal static async Task<string> ExtractBundleAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            byte[] bundle;
            await using (var resource = typeof(NodeBridgeLauncher).Assembly.GetManifestResourceStream(BundleResource)
                ?? throw new InvalidOperationException($"The plugin has no {BundleResource} resource."))
            using (var buffer = new MemoryStream())
            {
                await resource.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                bundle = buffer.ToArray();
            }

            var directory = Path.GetFullPath(Path.Combine(dataDirectory, "eufy-sdk"));
            Directory.CreateDirectory(directory);
            var name = $"bridge-{Convert.ToHexStringLower(SHA256.HashData(bundle))[..16]}.mjs";
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || new FileInfo(path).Length != bundle.Length)
            {
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, bundle, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }

            foreach (var old in Directory.EnumerateFiles(directory, "bridge-*.mjs").Where(f => !string.Equals(Path.GetFileName(f), name, StringComparison.Ordinal)))
            {
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // Another node may still run it; the next start removes it.
                }
            }

            return path;
        }

        private sealed class NodeBridgeConnection : IEufyBridgeConnection
        {
            private readonly Process _process;
            private readonly ILogger _logger;
            private readonly Channel<string> _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            private readonly SemaphoreSlim _writeLock = new(1, 1);
            private readonly Task _stdout;
            private readonly Task _stderr;

            public NodeBridgeConnection(Process process, ILogger logger)
            {
                _process = process;
                _logger = logger;
                _stdout = Task.Run(ReadStdoutAsync);
                _stderr = Task.Run(ReadStderrAsync);
                Exited = WaitForExitAsync();
            }

            public ChannelReader<string> Output => _output.Reader;
            public Task<int> Exited { get; }

            public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    // A closed stdin stops the bridge: it disconnects eufy-sdk and exits.
                    _process.StandardInput.Close();
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                }

                try
                {
                    await Exited.WaitAsync(StopGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("The eufy-sdk bridge did not stop in {Grace}; it is killed.", StopGrace);
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    await Exited.ConfigureAwait(false);
                }

                await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
                _process.Dispose();
                _writeLock.Dispose();
            }

            private async Task<int> WaitForExitAsync()
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                return _process.ExitCode;
            }

            private async Task ReadStdoutAsync()
            {
                try
                {
                    while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                        _output.Writer.TryWrite(line);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    _logger.LogDebug(error, "Reading the eufy-sdk bridge's output failed.");
                }
                finally
                {
                    _output.Writer.TryComplete();
                }
            }

            private async Task ReadStderrAsync()
            {
                try
                {
                    while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                        LogLine(line);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    _logger.LogDebug(error, "Reading the eufy-sdk bridge's log failed.");
                }
            }

            // The bridge writes "<level> <message>".
            private void LogLine(string line)
            {
                var space = line.IndexOf(' ');
                var (level, message) = space > 0 ? (line[..space], line[(space + 1)..]) : ("info", line);
                var logLevel = level switch
                {
                    "debug" => LogLevel.Debug,
                    "warn" => LogLevel.Warning,
                    "error" => LogLevel.Error,
                    "info" => LogLevel.Information,
                    _ => LogLevel.Warning
                };
                if (logLevel == LogLevel.Warning && level is not "warn")
                    message = line;
                _logger.Log(logLevel, "eufy-sdk: {Message}", message);
            }
        }
    }
}
