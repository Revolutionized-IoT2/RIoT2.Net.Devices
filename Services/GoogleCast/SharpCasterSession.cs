using Sharpcaster;
using Sharpcaster.Messages.Media;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;

namespace RIoT2.Net.Devices.Services.GoogleCast
{
    /// <summary>
    /// <see cref="ICastSession"/> over SharpCaster. SharpCaster's calls take no cancellation token, so each one is
    /// awaited with a timeout and the caller's token; a call that is given up on ends when the connection closes.
    /// </summary>
    public sealed class SharpCasterSession : ICastSession
    {
        /// <summary>The Default Media Receiver, Google's app that plays a media URL.</summary>
        public const string DefaultMediaReceiver = "CC1AD845";

        internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        private readonly ChromecastClient _client = new();
        private bool _connected;
        private bool _launched;

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var receiver = new ChromecastReceiver
            {
                Name = host,
                DeviceUri = new UriBuilder("https", host).Uri,
                Port = port
            };
            await CallAsync(() => _client.ConnectChromecast(receiver), ConnectTimeout, $"Could not connect to the speaker at {host}:{port}", cancellationToken)
                .ConfigureAwait(false);
            _connected = true;
        }

        public async Task<double?> GetVolumeAsync(CancellationToken cancellationToken)
        {
            var status = await CallAsync(() => _client.ReceiverChannel.GetChromecastStatusAsync(), RequestTimeout, "Could not read the speaker's volume", cancellationToken)
                .ConfigureAwait(false);
            return status?.Volume?.Level;
        }

        public Task SetVolumeAsync(double level, CancellationToken cancellationToken) =>
            CallAsync(() => _client.ReceiverChannel.SetVolume(Math.Clamp(level, 0, 1)), RequestTimeout, "Could not set the speaker's volume", cancellationToken);

        public async Task<bool> PlayAsync(CastMedia media, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            if (!_launched)
            {
                await CallAsync(() => _client.LaunchApplicationAsync(DefaultMediaReceiver, false), RequestTimeout, "The speaker didn't start its media player", cancellationToken)
                    .ConfigureAwait(false);
                _launched = true;
            }

            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = false;
            void OnStatus(object? sender, MediaStatus? status)
            {
                if (status == null)
                    return;
                if (status.PlayerState is PlayerStateType.Playing or PlayerStateType.Buffering or PlayerStateType.Paused)
                    started = true;
                else if (status.PlayerState == PlayerStateType.Idle && IsEnd(status, started))
                    ended.TrySetResult(true);
            }
            void OnFailed(object? sender, LoadFailedMessage message) =>
                ended.TrySetException(new CastException($"The speaker could not play {Describe(media)}."));
            void OnError(object? sender, ErrorMessage message) =>
                ended.TrySetException(new CastException($"The speaker could not play {Describe(media)} (error {message?.DetailedErrorCode})."));

            _client.MediaChannel.StatusChanged += OnStatus;
            _client.MediaChannel.LoadFailed += OnFailed;
            _client.MediaChannel.ErrorHappened += OnError;
            try
            {
                var item = new Media
                {
                    ContentId = media.Url,
                    ContentUrl = media.Url,
                    ContentType = media.ContentType,
                    StreamType = StreamType.Buffered,
                    Metadata = new MediaMetadata { MetadataType = MetadataType.Default, Title = media.Title }
                };
                var status = await CallAsync(() => _client.MediaChannel.LoadAsync(item), RequestTimeout, $"The speaker could not load {Describe(media)}", cancellationToken)
                    .ConfigureAwait(false);
                OnStatus(this, status);
                if (status?.PlayerState == PlayerStateType.Idle && status.IdleReason == "ERROR")
                    throw new CastException($"The speaker could not play {Describe(media)}.");

                try
                {
                    return await ended.Task.WaitAsync(maxWait, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return false;
                }
            }
            finally
            {
                _client.MediaChannel.StatusChanged -= OnStatus;
                _client.MediaChannel.LoadFailed -= OnFailed;
                _client.MediaChannel.ErrorHappened -= OnError;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var status = await CallAsync(() => _client.ReceiverChannel.GetChromecastStatusAsync(), RequestTimeout, "Could not read what the speaker plays", cancellationToken)
                .ConfigureAwait(false);
            if (status?.Applications == null || status.Applications.Count == 0)
                return;
            await CallAsync(() => _client.ReceiverChannel.StopApplication(), RequestTimeout, "Could not stop the speaker", cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_connected)
            {
                // After a failed connect SharpCaster's Dispose waits for a receive loop that never started. It closes the
                // socket before that wait, so it isn't awaited.
                _ = _client.Dispose();
                return;
            }
            try { await _client.DisconnectAsync().WaitAsync(RequestTimeout).ConfigureAwait(false); }
            catch (Exception) { /* the connection is going anyway */ }
        }

        // An idle state ends the media once it has started, or with a reason: FINISHED, CANCELLED, INTERRUPTED or ERROR.
        internal static bool IsEnd(MediaStatus status, bool started)
        {
            if (status.IdleReason == "ERROR")
                return true;
            return started || !string.IsNullOrEmpty(status.IdleReason);
        }

        private static string Describe(CastMedia media) =>
            Uri.TryCreate(media.Url, UriKind.Absolute, out var uri) ? $"the media from {uri.Host}" : "the media";

        private static async Task<T> CallAsync<T>(Func<Task<T>> call, TimeSpan timeout, string failure, CancellationToken cancellationToken)
        {
            try
            {
                return await call().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException error)
            {
                throw new CastException($"{failure}: no answer in {timeout.TotalSeconds:0} s.", error);
            }
            catch (CastException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new CastException($"{failure}: {error.GetBaseException().Message}", error);
            }
        }
    }
}
