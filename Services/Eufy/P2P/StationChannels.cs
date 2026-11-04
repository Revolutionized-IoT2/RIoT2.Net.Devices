// Ported from eufy-sdk src/transport/p2p/station-channels.ts, src/transport/p2p/lan-ip.ts and the family
// predicates of src/model/device-family.ts / src/model/device-types.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>Safe reads of the raw cloud <c>get_devs_list</c> entry.</summary>
    internal static class EufyRaw
    {
        public static JsonElement? Property(JsonElement raw, string name) =>
            raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var v) ? v : null;

        public static string? String(JsonElement raw, string name) =>
            Property(raw, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

        public static double? Number(JsonElement raw, string name) =>
            Property(raw, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;

        /// <summary><c>member.admin_user_id</c> when it is a string.</summary>
        public static string? AdminUserId(JsonElement raw) =>
            Property(raw, "member") is { ValueKind: JsonValueKind.Object } member ? String(member, "admin_user_id") : null;
    }

    /// <summary>A device's channel within its station, or why it has none.</summary>
    internal readonly record struct StationChannel(int? Channel, string? Issue = null, int? Claimed = null)
    {
        public static StationChannel Of(int channel) => new(channel);
        public static StationChannel Missing => new(null, "missing");
        public static StationChannel Shared(int claimed) => new(null, "shared", claimed);
    }

    /// <summary>Station topology and per-station channel addressing, from the device records alone.</summary>
    internal static class StationChannels
    {
        /// <summary>
        /// The station a device's traffic belongs to from its record: <c>parent_sn</c> wins, <c>station_sn</c> is the
        /// fallback, and a device naming no parent answers its own serial.
        /// </summary>
        public static string ResolvedStationSn(JsonElement raw, string sn)
        {
            var parent = EufyRaw.String(raw, "parent_sn");
            if (!string.IsNullOrEmpty(parent) && parent != sn) return parent;
            var station = EufyRaw.String(raw, "station_sn");
            return string.IsNullOrEmpty(station) ? sn : station;
        }

        /// <summary>The parent station a device's traffic belongs to: the session key the client opens.</summary>
        public static string StationOf(EufyDeviceRecord dev)
        {
            var parent = EufyRaw.String(dev.Raw, "parent_sn");
            if (!string.IsNullOrEmpty(parent) && parent != dev.Serial) return parent;
            return string.IsNullOrEmpty(dev.StationSerial) ? ResolvedStationSn(dev.Raw, dev.Serial) : dev.StationSerial;
        }

        /// <summary>The channel the record states: <see cref="EufyDeviceRecord.Channel"/>, else a numeric <c>device_channel</c>.</summary>
        public static int? StatedChannel(EufyDeviceRecord dev) =>
            dev.Channel ?? (EufyRaw.Number(dev.Raw, "device_channel") is { } c ? (int)c : null);

        /// <summary>
        /// The channel each device is addressed by within its station. A standalone device is its own station, on the
        /// channel its record states, or 0. An attached device has a channel only when its record states one that no
        /// other device attached to the same station also states.
        /// </summary>
        public static IReadOnlyDictionary<string, StationChannel> Resolve(IEnumerable<EufyDeviceRecord> devices)
        {
            var list = devices.ToList();
            var output = new Dictionary<string, StationChannel>();
            var claimants = new Dictionary<string, Dictionary<int, int>>();
            foreach (var d in list)
            {
                var station = StationOf(d);
                var stated = StatedChannel(d);
                if (d.Serial == station || stated is null) continue;
                if (!claimants.TryGetValue(station, out var counts)) claimants[station] = counts = [];
                counts[stated.Value] = counts.GetValueOrDefault(stated.Value) + 1;
            }
            foreach (var d in list)
            {
                var station = StationOf(d);
                var stated = StatedChannel(d);
                if (d.Serial == station) output[d.Serial] = StationChannel.Of(stated ?? 0);
                else if (stated is null) output[d.Serial] = StationChannel.Missing;
                else if (claimants.TryGetValue(station, out var c) && c.GetValueOrDefault(stated.Value) > 1) output[d.Serial] = StationChannel.Shared(stated.Value);
                else output[d.Serial] = StationChannel.Of(stated.Value);
            }
            return output;
        }
    }

    /// <summary>LAN-address resolution for a P2P station from its cloud device record.</summary>
    internal static partial class LanIp
    {
        [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$")]
        private static partial Regex Ipv4();

        /// <summary>True for an RFC-1918 private IPv4.</summary>
        public static bool IsPrivateIpv4(string s)
        {
            var m = Ipv4().Match(s);
            if (!m.Success) return false;
            var octets = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (octets.Any(o => o > 255)) return false;
            var (a, b) = (octets[0], octets[1]);
            return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
        }

        /// <summary>
        /// The freshest LAN IP for a station: the private-IPv4 param value with the newest <c>update_time</c>, else
        /// <c>local_ip</c> / <c>ip_addr</c> when private.
        /// </summary>
        public static string? FreshestLanIp(JsonElement raw)
        {
            string? best = null;
            double bestTs = 0;
            if (EufyRaw.Property(raw, "params") is { ValueKind: JsonValueKind.Array } ps)
            {
                foreach (var p in ps.EnumerateArray())
                {
                    var v = EufyRaw.String(p, "param_value");
                    if (v is null || !IsPrivateIpv4(v)) continue;
                    var ts = EufyRaw.Number(p, "update_time") ?? 0;
                    if (best is null || ts > bestTs) (best, bestTs) = (v, ts);
                }
            }
            if (best is not null) return best;
            foreach (var cand in new[] { EufyRaw.String(raw, "local_ip"), EufyRaw.String(raw, "ip_addr") })
                if (cand is not null && IsPrivateIpv4(cand)) return cand;
            return null;
        }
    }

    /// <summary>The model / device-type facts the station family predicates read.</summary>
    internal readonly record struct FamilyContext(int? DeviceType, string? Model);

    /// <summary>The device-family predicates that decide which transport serves a station.</summary>
    internal static class DeviceFamily
    {
        /// <summary>The vendor DeviceType of the HomeBase S1 Pro.</summary>
        public const int Station9000 = 27;

        /// <summary>HomeBase S1 Pro (<c>STATION_9000</c>), the station the app drives over its WebRTC control channel.</summary>
        public static bool IsStation9000(FamilyContext ctx) => ctx.DeviceType == Station9000;

        /// <summary>T8N00 NVR, identified by its reported model.</summary>
        public static bool IsNvrT8N00(FamilyContext ctx) => ctx.Model == "T8N00";

        /// <summary>Video Doorbell C31 (T8223) or C30 (T8224), streamed by the app over the portal <c>call</c> route.</summary>
        public static bool IsCallDoorbell(FamilyContext ctx) => ctx.Model is "T8223" or "T8224";
    }
}
