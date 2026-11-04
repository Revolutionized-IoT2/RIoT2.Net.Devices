// Ported from eufy-sdk src/model/capabilities/{motion,person-detection,doorbell,arming}.ts (push `events` tables),
// src/model/capabilities/index.ts (decodeEvent, push source) and src/client/eufy-mega.ts (startPush signal)
// @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>
    /// Maps a normalised push to the semantic event a capability module's push table emits for its event type.
    /// </summary>
    /// <remarks>
    /// Upstream resolves a push id against the device's capabilities only when two capability families claim
    /// the same id (<c>resolveHits</c>); this port has no capability resolution and decodes by event type alone.
    /// That is the same result for every id here: upstream's own test pins the set of contested ids as empty,
    /// and the families that reuse some of these integers (SmartDrop's 10 and 3102) map no push events. Each id
    /// maps to exactly one event, so one push never yields two. Upstream's event <c>claim</c>s only narrow a
    /// manifest's description, never dispatch, so they are not needed here. The <c>refresh</c> of
    /// <c>armingModeChanged</c> (re-read the guard mode) is left to the consumer, as the contract says.
    /// </remarks>
    internal static class PushEventDecoder
    {
        private static readonly Dictionary<int, EufyEventKind> PushTable = new()
        {
            // motion.ts
            [(int)DoorbellPushEvent.MOTION_DETECTION] = EufyEventKind.Motion,
            [(int)CusPushEvent.MOTION_SENSOR_PIR] = EufyEventKind.Motion,
            [(int)IndoorPushEvent.CRYING_DETECTION] = EufyEventKind.CryingDetected,
            [(int)IndoorPushEvent.SOUND_DETECTION] = EufyEventKind.SoundDetected,
            [(int)IndoorPushEvent.PET_DETECTION] = EufyEventKind.PetDetected,
            [(int)DoorbellPushEvent.VEHICLE_DETECTION] = EufyEventKind.VehicleDetected,
            [(int)HB3PairedDevicePushEvent.DOG_DETECTION] = EufyEventKind.DogDetected,
            // upstream adds kind "lick" / "poop"; EufySecurityEvent has no field for it.
            [(int)HB3PairedDevicePushEvent.DOG_LICK_DETECTION] = EufyEventKind.DogDetected,
            [(int)HB3PairedDevicePushEvent.DOG_POOP_DETECTION] = EufyEventKind.DogDetected,
            // person-detection.ts
            [(int)DoorbellPushEvent.FACE_DETECTION] = EufyEventKind.PersonDetected,
            [(int)HB3PairedDevicePushEvent.IDENTITY_PERSON_DETECTION] = EufyEventKind.PersonDetected,
            [(int)HB3PairedDevicePushEvent.STRANGER_PERSON_DETECTION] = EufyEventKind.StrangerDetected,
            // doorbell.ts
            [(int)DoorbellPushEvent.PRESS_DOORBELL] = EufyEventKind.DoorbellPress,
            [(int)DoorbellPushEvent.PACKAGE_DELIVERED] = EufyEventKind.PackageDelivered,
            [(int)DoorbellPushEvent.PACKAGE_TAKEN] = EufyEventKind.PackageTaken,
            [(int)DoorbellPushEvent.PACKAGE_STRANDED] = EufyEventKind.PackageStranded,
            // arming.ts
            [(int)CusPushEvent.MODE_SWITCH] = EufyEventKind.ArmingModeChanged,
            [(int)CusPushEvent.ALARM] = EufyEventKind.Alarm,
            [(int)CusPushEvent.ALARM_DELAY] = EufyEventKind.Alarm,
        };

        /// <summary>The semantic event of a push, or null when its event type maps to none of the supported kinds.</summary>
        public static EufySecurityEvent? Decode(PushEvent push)
        {
            if (push.EventType is not { } eventType || !PushTable.TryGetValue(eventType, out var kind)) return null;
            var payload = push.Payload;
            return new EufySecurityEvent
            {
                Kind = kind,
                DeviceSerial = push.DeviceSn,
                StationSerial = push.StationSn,
                ThumbnailUrl = push.ThumbnailCandidate?.Url,
                PersonName = kind == EufyEventKind.PersonDetected ? PersonName(payload) : null,
                AlarmPhase = kind == EufyEventKind.Alarm ? AlarmPhase(eventType, payload) : null,
                AlarmType = kind == EufyEventKind.Alarm ? AlarmType(payload) : null,
                EventType = eventType,
            };
        }

        /// <summary>
        /// arming.ts: ALARM_DELAY is <c>"delayed"</c>; ALARM is <c>"stopped"</c> when the push's <c>type</c> is
        /// <c>HUB_STOP_BY_APP</c> (<c>decodeAlarmPhase</c>), otherwise <c>"triggered"</c>.
        /// </summary>
        private static EufyAlarmPhase AlarmPhase(int eventType, JsonObject payload)
        {
            if (eventType == (int)CusPushEvent.ALARM_DELAY) return EufyAlarmPhase.Delayed;
            return PushNormalizer.AsInt(payload["type"]) == (int)CusPushAlarmType.HUB_STOP_BY_APP ? EufyAlarmPhase.Stopped : EufyAlarmPhase.Triggered;
        }

        /// <summary>The alarm source / stop reason: the push's <c>type</c> (what upstream reads), else its <c>alarm_type</c>.</summary>
        private static int? AlarmType(JsonObject payload) => PushNormalizer.AsInt(payload["type"]) ?? PushNormalizer.AsInt(payload["alarm_type"]);

        /// <summary>
        /// Not upstream (its decoded payload just carries the raw body): the recognised names a v6 push lists in
        /// <c>objects.names</c>, comma-joined, else a <c>person_name</c> string.
        /// </summary>
        private static string? PersonName(JsonObject payload)
        {
            if (payload["objects"] is JsonObject objects && objects["names"] is JsonArray names)
            {
                var list = names.Select(PushNormalizer.NonEmptyString).OfType<string>().ToList();
                if (list.Count > 0) return string.Join(",", list);
            }
            return PushNormalizer.NonEmptyString(payload["person_name"]);
        }
    }
}
