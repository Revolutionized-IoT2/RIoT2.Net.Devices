// Ported from eufy-sdk src/transport/push/push-client.ts (normalizePushEvent, handleDataMessage) @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>Decodes a <c>DataMessageStanza</c> into the eufy envelope and normalises it, without device semantics.</summary>
    internal static class PushNormalizer
    {
        /// <summary>
        /// The raw push: <c>payload</c> carries the whole app_data envelope, with its <c>payload</c> entry
        /// (base64 of NUL-terminated JSON) parsed in place; the envelope's own keys sit beside that entry.
        /// </summary>
        public static RawPushMessage ToRawMessage(McsDataMessageStanza stanza)
        {
            var data = new JsonObject();
            foreach (var kv in stanza.AppData)
            {
                if (kv.Key == "payload")
                {
                    var json = ReadNullTerminated(DecodeBase64Lenient(kv.Value));
                    data["payload"] = TryParseJson(json, out var parsed) ? parsed : JsonValue.Create(json);
                }
                else
                {
                    data[kv.Key] = JsonValue.Create(kv.Value);
                }
            }
            return new RawPushMessage
            {
                Id = stanza.Id,
                From = stanza.From,
                To = stanza.To,
                Category = stanza.Category,
                PersistentId = stanza.PersistentId,
                Ttl = stanza.Ttl,
                Sent = stanza.Sent,
                Payload = data,
            };
        }

        /// <summary>
        /// Normalises a decoded eufy envelope. The detail (<c>event_type</c>, <c>pic_url</c>, <c>cipher</c>) is
        /// read from the deepest level of <see cref="PayloadLevels"/>; identity (<c>device_sn</c>,
        /// <c>station_sn</c>) is gathered from every level, deepest first.
        /// </summary>
        public static PushEvent Normalize(RawPushMessage raw)
        {
            var levels = PayloadLevels(raw.Payload);
            var p = levels[^1];
            var deepestFirst = Enumerable.Reverse(levels).ToList();
            var deviceClaims = deepestFirst.Select(l => NonEmptyString(l["device_sn"])).OfType<string>().ToList();
            var levelStations = deepestFirst.Select(l => NonEmptyString(l["station_sn"])).OfType<string>().ToList();
            var shortStation = NonEmptyString(p["s"]);
            var stationClaims = shortStation is null ? levelStations : [.. levelStations, shortStation];
            // `event_type ?? a`: a present non-number event_type yields no type, as upstream's typeof check does.
            var eventType = AsInt(p["event_type"] ?? p["a"]);
            var url = NonEmptyString(p["pic_url"]) ?? NonEmptyString(p["thumbnail"]);
            ThumbnailCandidate? candidate = null;
            if (url is not null)
            {
                var deviceSn = deviceClaims.FirstOrDefault();
                var stationSn = stationClaims.FirstOrDefault();
                ThumbnailCandidateAttribution attribution;
                if (deviceSn is not null && deviceClaims.All(c => c == deviceSn))
                    attribution = new(ThumbnailAttributionKind.Device, DeviceSn: deviceSn);
                else if (deviceClaims.Count == 0 && stationSn is not null)
                    attribution = new(ThumbnailAttributionKind.Station, StationSn: stationClaims.All(c => c == stationSn) ? stationSn : null);
                else
                    attribution = new(ThumbnailAttributionKind.Ambiguous);
                candidate = new ThumbnailCandidate(url, attribution);
            }
            return new PushEvent
            {
                DeviceSn = deviceClaims.FirstOrDefault() ?? shortStation,
                StationSn = levelStations.FirstOrDefault(),
                EventType = eventType,
                ThumbnailUrl = AsString(p["pic_url"] ?? p["thumbnail"]),
                ThumbnailCandidate = candidate,
                Cipher = AsInt(p["cipher"] ?? p["k"]),
                Payload = p,
                Raw = raw,
            };
        }

        /// <summary>
        /// The levels of a decoded push, outermost first: the envelope, then each nested <c>payload</c> in turn,
        /// JSON-parsed where it arrives as a string. A <c>payload</c> that is present but not an object
        /// (unparseable text, a scalar) ends the walk with an empty level.
        /// </summary>
        internal static List<JsonObject> PayloadLevels(JsonObject envelope)
        {
            var levels = new List<JsonObject> { envelope };
            for (var level = envelope; level["payload"] is { } next;)
            {
                if (next is JsonValue value && value.TryGetValue<string>(out var text))
                    next = TryParseJson(text, out var parsed) ? parsed : null;
                level = next as JsonObject ?? [];
                levels.Add(level);
            }
            return levels;
        }

        internal static string? NonEmptyString(JsonNode? node) =>
            AsString(node) is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;

        internal static string? AsString(JsonNode? node) =>
            node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

        /// <summary>A JSON number that is an integer, as an int; anything else is no value.</summary>
        internal static int? AsInt(JsonNode? node)
        {
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
            if (v.TryGetValue<JsonElement>(out var e) && e.TryGetInt32(out var ei)) return ei;
            return null;
        }

        private static bool TryParseJson(string text, out JsonNode? node)
        {
            try
            {
                node = JsonNode.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                node = null;
                return false;
            }
        }

        private static string ReadNullTerminated(byte[] buffer)
        {
            var i = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, i == -1 ? buffer.Length : i);
        }

        /// <summary>Decodes base64 as leniently as Node's <c>Buffer.from(s, "base64")</c>: url-safe characters, no padding, stray characters skipped.</summary>
        internal static byte[] DecodeBase64Lenient(string value)
        {
            var sb = new StringBuilder(value.Length + 3);
            foreach (var c in value)
            {
                if (c == '=') break;
                if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/') sb.Append(c);
                else if (c == '-') sb.Append('+');
                else if (c == '_') sb.Append('/');
            }
            if (sb.Length % 4 == 1) sb.Length--;
            while (sb.Length % 4 != 0) sb.Append('=');
            try
            {
                return Convert.FromBase64String(sb.ToString());
            }
            catch (FormatException)
            {
                return [];
            }
        }
    }
}
