// Ported from eufy-sdk src/transport/push/store.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>FCM credentials and the seen persistent ids, persisted so the token is registered once.</summary>
    internal sealed record PersistedPush
    {
        [JsonPropertyName("creds")]
        public required FcmCredentials Creds { get; init; }
        [JsonPropertyName("persistentIds")]
        public IReadOnlyList<string> PersistentIds { get; init; } = [];
    }

    internal interface IFcmStore
    {
        /// <summary>The persisted state, or null when there is none or it is unusable.</summary>
        Task<PersistedPush?> LoadAsync(CancellationToken cancellationToken);
        Task SaveAsync(PersistedPush value, CancellationToken cancellationToken);
        void Clear();
    }

    internal sealed class MemoryFcmStore : IFcmStore
    {
        private PersistedPush? _value;

        public Task<PersistedPush?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_value);

        public Task SaveAsync(PersistedPush value, CancellationToken cancellationToken)
        {
            _value = value;
            return Task.CompletedTask;
        }

        public void Clear() => _value = null;
    }

    /// <summary>
    /// A JSON file per account: <c>&lt;dir&gt;/eufy-push-&lt;first 16 hex of SHA-256(lowercased trimmed email)&gt;.json</c>,
    /// written through a temporary file and a move, owner-only on Unix.
    /// </summary>
    internal sealed class FileFcmStore : IFcmStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly SemaphoreSlim _gate = new(1, 1);

        public FileFcmStore(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>The store for an account; the file name carries a hash of the email, never the email.</summary>
        public static FileFcmStore ForAccount(string dataDirectory, string email) =>
            new(System.IO.Path.Combine(dataDirectory, FileNameFor(email)));

        public static string FileNameFor(string email)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
            return $"eufy-push-{Convert.ToHexStringLower(hash)[..16]}.json";
        }

        public async Task<PersistedPush?> LoadAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(Path)) return null;
                var stream = File.OpenRead(Path);
                await using (stream.ConfigureAwait(false))
                {
                    var value = await JsonSerializer.DeserializeAsync<PersistedPush>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (value is null || !IsValid(value)) return null;
                    return value with { PersistentIds = value.PersistentIds?.Where(id => !string.IsNullOrEmpty(id)).ToList() ?? [] };
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveAsync(PersistedPush value, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using (stream.ConfigureAwait(false))
                    {
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                    File.Move(temp, Path, overwrite: true);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Clear() => TryDelete(Path);

        private static bool IsValid(PersistedPush? value) =>
            value?.Creds is { } c
            && !string.IsNullOrWhiteSpace(c.Fid)
            && ulong.TryParse(c.AndroidId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(c.SecurityToken, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            && !string.IsNullOrWhiteSpace(c.FcmToken);

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // gone or locked; nothing to recover
            }
        }
    }
}
