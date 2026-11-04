// Ported from eufy-sdk src/transport/push/types.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>Raw MCS frame: a tag and its decoded protobuf object (null for tags without a schema).</summary>
    internal sealed record McsMessage(int Tag, object? Message);

    /// <summary>
    /// Outer FCM message after <c>app_data</c> extraction. <see cref="Payload"/> is the eufy envelope: the
    /// push's <c>app_data</c> entries (string values), with the <c>payload</c> entry (base64 of NUL-terminated
    /// JSON) parsed in place, or kept as text when it is not JSON.
    /// </summary>
    internal sealed class RawPushMessage
    {
        public string? Id { get; init; }
        public string? From { get; init; }
        public string? To { get; init; }
        public string? Category { get; init; }
        public string? PersistentId { get; init; }
        public int? Ttl { get; init; }
        public long? Sent { get; init; }
        public JsonObject Payload { get; init; } = [];
    }

    internal enum ThumbnailAttributionKind
    {
        Device,
        Station,
        Ambiguous,
    }

    /// <summary>Facts available to attribute a push-provided thumbnail candidate.</summary>
    internal sealed record ThumbnailCandidateAttribution(ThumbnailAttributionKind Kind, string? DeviceSn = null, string? StationSn = null);

    /// <summary>A push-provided thumbnail URL together with its transport-level attribution facts.</summary>
    internal sealed record ThumbnailCandidate(string Url, ThumbnailCandidateAttribution Attribution);

    /// <summary>A normalised push event (capability-blind; see <see cref="PushEventDecoder"/> for the semantic event).</summary>
    internal sealed class PushEvent
    {
        /// <summary>Best-effort device serial (falls back to the short station <c>s</c>).</summary>
        public string? DeviceSn { get; init; }
        public string? StationSn { get; init; }
        /// <summary>Numeric event type as the cloud sends it (<c>event_type</c>, else <c>a</c>).</summary>
        public int? EventType { get; init; }
        /// <summary>Resolved event name (<see cref="PushEvents.DetectionName"/>).</summary>
        public string? EventName { get; set; }
        /// <summary>The thumbnail URL, if the push carried one (<c>pic_url</c>, else <c>thumbnail</c>).</summary>
        public string? ThumbnailUrl { get; init; }
        /// <summary>A usable thumbnail URL and the identity claims carried beside it.</summary>
        public ThumbnailCandidate? ThumbnailCandidate { get; init; }
        /// <summary>Cipher id for decoding the media (when present).</summary>
        public int? Cipher { get; init; }
        /// <summary>The deepest parsed payload level.</summary>
        public JsonObject Payload { get; init; } = [];
        /// <summary>The whole raw push (envelope + app_data).</summary>
        public RawPushMessage Raw { get; init; } = new();
    }

    /// <summary>Persisted FCM credentials so the client registers once and reconnects across runs.</summary>
    internal sealed record FcmCredentials
    {
        [JsonPropertyName("fid")]
        public required string Fid { get; init; }
        [JsonPropertyName("androidId")]
        public required string AndroidId { get; init; }
        [JsonPropertyName("securityToken")]
        public required string SecurityToken { get; init; }
        /// <summary>The FCM registration token (what is handed to eufy's cloud).</summary>
        [JsonPropertyName("fcmToken")]
        public required string FcmToken { get; init; }
        /// <summary>Firebase installation refresh token (for auth-token renewal).</summary>
        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; init; }
        /// <summary>Unix milliseconds.</summary>
        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; init; }

        /// <summary>Redacted: the record holds secrets and must never reach a log.</summary>
        public override string ToString() => "FcmCredentials { <redacted> }";
    }
}
