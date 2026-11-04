using System.Text.Json;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>
    /// One push of the Netatmo webhook. The payload isn't versioned, so it is read leniently: every member is optional,
    /// and ids are looked for where pyatmo (Home Assistant's Netatmo library) finds them.
    /// </summary>
    public sealed class NetatmoWebhookEvent
    {
        public string? EventType { get; init; }
        public string? PushType { get; init; }
        public string? HomeId { get; init; }
        public string? ModuleId { get; init; }
        public string? CameraId { get; init; }
        public string? DeviceId { get; init; }
        public string? EventId { get; init; }
        public string? SubType { get; init; }
        public string? Message { get; init; }
        public string? SnapshotUrl { get; init; }
        public string? VignetteUrl { get; init; }
        /// <summary>The floodlight mode of a <c>light_mode</c> event.</summary>
        public string? Mode { get; init; }
        public string? PersonId { get; init; }
        public long? Time { get; init; }
        public IReadOnlyList<NetatmoWebhookPerson> Persons { get; init; } = [];

        public bool IsActivation => PushType == "webhook_activation";
        public bool IsDeactivation => PushType == "webhook_deactivation";

        /// <summary>The ids that may name the module the event is about, most specific first.</summary>
        public IEnumerable<string> ModuleCandidates =>
            new[] { ModuleId, DeviceId, CameraId }.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!);

        /// <summary>The push, or null when the body isn't a JSON object.</summary>
        public static NetatmoWebhookEvent? Parse(string json)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;
                var extra = root.TryGetProperty("extra_params", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;

                var persons = new List<NetatmoWebhookPerson>();
                if (root.TryGetProperty("persons", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var person in list.EnumerateArray())
                    {
                        var id = Text(person, "id");
                        if (id != null)
                            persons.Add(new NetatmoWebhookPerson(id, Bool(person, "is_known"), Text(person, "face_url")));
                    }
                }

                string? homeId = Text(root, "home_id");
                if (homeId == null && root.TryGetProperty("home", out var home) && home.ValueKind == JsonValueKind.Object)
                    homeId = Text(home, "id");

                var pushType = Text(root, "push_type");
                return new NetatmoWebhookEvent
                {
                    // A push names its type in event_type; some name it only in push_type, as "<module type>-<event type>".
                    EventType = Text(root, "event_type") ?? Text(extra, "event_type") ?? Text(extra, "type") ?? TypeOfPush(pushType),
                    PushType = pushType,
                    HomeId = homeId,
                    ModuleId = Text(root, "module_id") ?? Text(extra, "module_id"),
                    CameraId = Text(root, "camera_id"),
                    DeviceId = Text(root, "device_id"),
                    EventId = Text(root, "event_id"),
                    SubType = Text(root, "sub_type"),
                    Message = Text(root, "message"),
                    SnapshotUrl = Text(root, "snapshot_url"),
                    VignetteUrl = Text(root, "vignette_url"),
                    Mode = Text(root, "mode") ?? Text(extra, "mode"),
                    PersonId = Text(root, "person_id"),
                    Time = Number(root, "time"),
                    Persons = persons
                };
            }
        }

        private static string? TypeOfPush(string? pushType)
        {
            if (pushType == null || pushType.StartsWith("webhook_", StringComparison.Ordinal))
                return null;
            var dash = pushType.IndexOf('-');
            return dash > 0 && dash < pushType.Length - 1 ? pushType[(dash + 1)..] : null;
        }

        private static string? Text(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrEmpty(value.GetString()) ? null : value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        private static bool? Bool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean() : null;

        private static long? Number(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
                ? number : null;
    }

    public sealed record NetatmoWebhookPerson(string Id, bool? IsKnown, string? FaceUrl);

    /// <summary>
    /// A security event, from the webhook or from <c>getevents</c>, in one shape: both go through the same handling and
    /// the same de-duplication.
    /// </summary>
    public sealed record NetatmoSecurityEvent
    {
        public string? Id { get; init; }
        public required string Type { get; init; }
        public string? ModuleId { get; init; }
        /// <summary>Seconds since the epoch; null when Netatmo didn't say.</summary>
        public long? Time { get; init; }
        public string? Message { get; init; }
        public string? SnapshotUrl { get; init; }
        public string? VignetteUrl { get; init; }
        public string? PersonId { get; init; }
        public bool? IsKnown { get; init; }
        public string? Mode { get; init; }
        /// <summary>Netatmo's detail of the event, for example whether smoke was detected or cleared.</summary>
        public string? SubType { get; init; }

        /// <summary>The key that tells a repeat apart: the same event from the webhook and from a poll has the same one.</summary>
        public string DeduplicationKey(string category) =>
            Id != null ? $"{Id}|{category}|{PersonId}" : $"{ModuleId}|{Type}|{Time}|{PersonId}";

        /// <summary>Every event a <c>getevents</c> event carries: an outdoor camera's event holds one per detection.</summary>
        public static IEnumerable<NetatmoSecurityEvent> From(Event polled)
        {
            if (polled.subevents != null && polled.subevents.Count > 0)
            {
                foreach (var sub in polled.subevents)
                {
                    yield return new NetatmoSecurityEvent
                    {
                        Id = polled.id,
                        Type = string.IsNullOrEmpty(sub.type) ? polled.type : sub.type,
                        ModuleId = polled.module_id,
                        Time = sub.time > 0 ? sub.time : polled.time,
                        Message = sub.message ?? polled.message,
                        SnapshotUrl = sub.snapshot?.url,
                        VignetteUrl = sub.vignette?.url
                    };
                }
                yield break;
            }

            yield return new NetatmoSecurityEvent
            {
                Id = polled.id,
                Type = polled.type,
                ModuleId = polled.module_id,
                Time = polled.time > 0 ? polled.time : null,
                Message = polled.message,
                SnapshotUrl = polled.snapshot?.url,
                VignetteUrl = polled.vignette?.url,
                PersonId = polled.person_id,
                // getevents doesn't say whether a face is known; the device tells from the home's named persons.
                IsKnown = null
            };
        }

        /// <summary>
        /// The events a webhook push carries; a <c>person</c> push names every person seen. <paramref name="moduleId"/>
        /// is the module the push is about, resolved against the home's modules.
        /// </summary>
        public static IEnumerable<NetatmoSecurityEvent> From(NetatmoWebhookEvent pushed, string? moduleId)
        {
            if (pushed.EventType == null)
                yield break;

            var common = new NetatmoSecurityEvent
            {
                Id = pushed.EventId,
                Type = pushed.EventType,
                ModuleId = moduleId,
                Time = pushed.Time,
                Message = pushed.Message,
                SnapshotUrl = pushed.SnapshotUrl,
                VignetteUrl = pushed.VignetteUrl,
                PersonId = pushed.PersonId,
                Mode = pushed.Mode,
                SubType = pushed.SubType
            };

            if (pushed.Persons.Count == 0)
            {
                yield return common;
                yield break;
            }

            foreach (var person in pushed.Persons)
                yield return common with { PersonId = person.Id, IsKnown = person.IsKnown };
        }
    }
}
