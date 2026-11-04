namespace RIoT2.Net.Devices.Services.GoogleCast
{
    /// <summary>Media for a Cast speaker: a URL it downloads itself.</summary>
    public sealed record CastMedia(string Url, string ContentType, string Title);

    /// <summary>
    /// One connection to a Cast speaker, for one command. The GoogleCast device creates it through a seam the tests
    /// replace; <see cref="SharpCasterSession"/> is the real one.
    /// </summary>
    public interface ICastSession : IAsyncDisposable
    {
        Task ConnectAsync(string host, int port, CancellationToken cancellationToken);

        /// <summary>The speaker's volume, 0-1, or null when it doesn't tell.</summary>
        Task<double?> GetVolumeAsync(CancellationToken cancellationToken);

        /// <summary>Sets the speaker's volume, 0-1.</summary>
        Task SetVolumeAsync(double level, CancellationToken cancellationToken);

        /// <summary>
        /// Plays <paramref name="media"/> in the Default Media Receiver and waits until it has ended. Returns false when it
        /// still plays after <paramref name="maxWait"/>; throws when the speaker can't play it.
        /// </summary>
        Task<bool> PlayAsync(CastMedia media, TimeSpan maxWait, CancellationToken cancellationToken);

        /// <summary>Stops the app the speaker runs, whichever it is.</summary>
        Task StopAsync(CancellationToken cancellationToken);
    }

    /// <summary>The speaker couldn't be reached, or refused what it was sent.</summary>
    public sealed class CastException(string message, Exception? inner = null) : Exception(message, inner);
}
