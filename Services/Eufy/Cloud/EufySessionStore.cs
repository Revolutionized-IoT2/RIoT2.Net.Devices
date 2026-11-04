// Ported from eufy-sdk src/core/store.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Services.Eufy.Cloud
{
    /// <summary>
    /// The persisted session: the auth token and user, the region shard, the device identity and the negotiated
    /// ECDH key, so a restart goes straight to data calls (no estimate_domain, key exchange, login or 2FA).
    /// </summary>
    internal sealed record PersistedSession
    {
        public required string UserId { get; init; }
        /// <summary>The account's own <c>user_id</c>, which the gtoken header is hashed from. A record without it is not restored.</summary>
        public string? AccountUserId { get; init; }
        public required string AuthToken { get; init; }
        public string? GeoKey { get; init; }
        public required string Region { get; init; }
        /// <summary>The country the session was logged in with; another configured country logs in again.</summary>
        public string? CountryCode { get; init; }
        public string? EstimatedDomain { get; init; }
        public required string Openudid { get; init; }
        public string? PhoneModel { get; init; }
        public string? MediaUserAgent { get; init; }
        /// <summary>The shareKey hex (32 chars) and its bound key-ident.</summary>
        public required string ShareKey { get; init; }
        public required string KeyIdent { get; init; }
        /// <summary>Unix seconds when the auth token expires (0 = unknown).</summary>
        public long TokenExpiresAt { get; init; }
        /// <summary>Unix milliseconds of the save.</summary>
        public long SavedAt { get; init; }
    }

    /// <summary>Where a <see cref="PersistedSession"/> lives across runs.</summary>
    internal interface IEufySessionStore
    {
        PersistedSession? Load();
        void Save(PersistedSession session);
        void Clear();
    }

    /// <summary>An in-memory store (no persistence).</summary>
    internal sealed class MemoryEufySessionStore : IEufySessionStore
    {
        private PersistedSession? _session;

        public PersistedSession? Load() => _session;
        public void Save(PersistedSession session) => _session = session;
        public void Clear() => _session = null;
    }

    /// <summary>
    /// A JSON file store at <c>&lt;dataDirectory&gt;/eufy-session-&lt;hash&gt;.json</c>; the name carries a hash of the
    /// account, never the email. Written through a temp file and a move, and readable by the owner only on Unix.
    /// </summary>
    internal sealed class FileEufySessionStore : IEufySessionStore
    {
        private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        private readonly Lock _gate = new();

        public FileEufySessionStore(string path)
        {
            Path = path;
        }

        public string Path { get; }

        /// <summary>The session file of <paramref name="email"/> in <paramref name="dataDirectory"/>.</summary>
        public static FileEufySessionStore ForAccount(string dataDirectory, string email) =>
            new(System.IO.Path.Combine(dataDirectory, FileName(email)));

        /// <summary><c>eufy-session-&lt;first 16 hex of SHA-256(lowercased, trimmed email)&gt;.json</c>.</summary>
        public static string FileName(string email)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
            return $"eufy-session-{Convert.ToHexStringLower(hash)[..16]}.json";
        }

        public PersistedSession? Load()
        {
            lock (_gate)
            {
                try
                {
                    return JsonSerializer.Deserialize<PersistedSession>(File.ReadAllText(Path), SerializerOptions);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    return null;
                }
            }
        }

        public void Save(PersistedSession session)
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                var temp = Path + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".tmp";
                try
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows())
                        options.UnixCreateMode = OwnerOnly;
                    using (var stream = new FileStream(temp, options))
                    {
                        JsonSerializer.Serialize(stream, session, SerializerOptions);
                    }
                    File.Move(temp, Path, overwrite: true);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(Path, OwnerOnly);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                try
                {
                    File.Delete(Path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // already gone or not ours to remove
                }
            }
        }
    }

    internal static class EufySessionValidity
    {
        /// <summary>The seconds before its expiry a token stops being used.</summary>
        public const int DefaultSkewSeconds = 300;

        /// <summary>A token is usable if it has no known expiry, or expires more than <paramref name="skewSeconds"/> from now.</summary>
        public static bool TokenNotExpired(long tokenExpiresAt, DateTimeOffset now, int skewSeconds = DefaultSkewSeconds) =>
            tokenExpiresAt <= 0 || now.ToUnixTimeSeconds() < tokenExpiresAt - skewSeconds;

        /// <summary>
        /// A persisted session is usable if it carries a complete credential (token, bound ECDH key and the account id
        /// the gtoken header is hashed from) whose token is not (nearly) expired.
        /// </summary>
        public static bool IsSessionValid(PersistedSession? s, DateTimeOffset now, int skewSeconds = DefaultSkewSeconds)
        {
            if (s is null || string.IsNullOrEmpty(s.AuthToken) || string.IsNullOrEmpty(s.ShareKey) || string.IsNullOrEmpty(s.KeyIdent) || string.IsNullOrEmpty(s.AccountUserId))
                return false;
            return TokenNotExpired(s.TokenExpiresAt, now, skewSeconds);
        }
    }
}
