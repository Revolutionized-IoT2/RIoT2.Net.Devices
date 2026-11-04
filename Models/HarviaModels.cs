using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>
    /// The bits of a Xenio panel's <c>statusCodes</c>, as verified by the Home Assistant community on three devices
    /// (WiesiDeluxe/ha-harvia-sauna, issue #6). The other bits are device specific.
    /// </summary>
    internal static class HarviaStatus
    {
        public const int DoorOpen = 1;
        public const int TargetReached = 5;
        public const int HeatDemand = 8;
        public const int Light = 11;
        public const int SessionActive = 17;
        public const int Stopped = 18;

        public static bool? Bit(int? codes, int bit) => codes is int value ? (value & (1 << bit)) != 0 : null;
    }

    /// <summary>
    /// The panel's own one-shot schedule (<c>timedStart</c>): 8 bytes, base64. Byte 0 is the enabled flag, byte 1 the
    /// duration in 15-minute units, byte 2 the target °C, bytes 4-7 the Unix time the sauna is ready (little-endian).
    /// The heater ignites at the ready time minus its heat-up time. All zeros means no schedule.
    /// </summary>
    internal sealed record HarviaSchedule(bool Enabled, DateTimeOffset ReadyAt, int DurationMinutes, int TargetTemperature)
    {
        public static HarviaSchedule? Decode(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return null;
            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
            if (raw.Length != 8 || raw.All(x => x == 0))
                return null;
            var epoch = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
            return new HarviaSchedule(raw[0] != 0, DateTimeOffset.FromUnixTimeSeconds(epoch), raw[1] * 15, raw[2]);
        }
    }

    /// <summary>
    /// One sauna: its reported state (the device shadow) merged with its latest telemetry. The cloud pushes only what
    /// changed, so both are merged into what is known.
    /// </summary>
    internal sealed class HarviaSauna
    {
        public HarviaSauna(string id) => Id = id;

        public string Id { get; }

        public string? DisplayName { get; private set; }

        public string Name => string.IsNullOrWhiteSpace(DisplayName) ? "Sauna" : DisplayName!;

        /// <summary>The sauna is switched on (<c>active</c>).</summary>
        public bool? Active { get; private set; }

        public bool? Light { get; private set; }

        public int? TargetTemperature { get; private set; }

        /// <summary>The heat-up time the panel uses for its schedule, in minutes.</summary>
        public int? HeatUpMinutes { get; private set; }

        public int? StatusCodes { get; private set; }

        public string? TimedStart { get; private set; }

        public double? Temperature { get; private set; }

        public double? Humidity { get; private set; }

        public int? RemainingMinutes { get; private set; }

        /// <summary>The telemetry's heater flag.</summary>
        public bool? HeatOn { get; private set; }

        /// <summary>When the cloud last heard from the panel.</summary>
        public DateTimeOffset? LastSeen { get; private set; }

        public bool? DoorOpen => HarviaStatus.Bit(StatusCodes, HarviaStatus.DoorOpen);

        public bool? TargetReached => HarviaStatus.Bit(StatusCodes, HarviaStatus.TargetReached);

        /// <summary>
        /// The element is heating up: switched on, heat demand (bit 8) and the target not yet reached (bit 5). Bit 8
        /// alone stays set for the whole session. Without status codes, the telemetry's heater flag.
        /// </summary>
        public bool? Heating
        {
            get
            {
                if (Active == false)
                    return false;
                if (StatusCodes != null)
                    return Active == true && HarviaStatus.Bit(StatusCodes, HarviaStatus.HeatDemand) == true && TargetReached != true;
                return HeatOn;
            }
        }

        /// <summary><c>off</c>, <c>heating</c>, <c>ready</c> (the target reached) or <c>stopped</c>.</summary>
        public string? StateName
        {
            get
            {
                if (Active == null)
                    return null;
                if (Active == false)
                    return HarviaStatus.Bit(StatusCodes, HarviaStatus.Stopped) == true ? Stopped : Off;
                return TargetReached == true ? Ready : HeatingState;
            }
        }

        public const string Off = "off";
        public const string HeatingState = "heating";
        public const string Ready = "ready";
        public const string Stopped = "stopped";

        public static readonly string[] StateNames = [Off, HeatingState, Ready, Stopped];

        public HarviaSchedule? Schedule => HarviaSchedule.Decode(TimedStart);

        /// <summary>When telemetry first said the heater is on while the state said off; a hint that a push was lost.</summary>
        public DateTimeOffset? HeatingWhileOffSince { get; set; }

        /// <summary>Merges the reported state, or a pushed part of it.</summary>
        public void MergeState(JsonObject reported, DateTimeOffset? at)
        {
            if (reported.ContainsKey("displayName"))
                DisplayName = HarviaJson.Text(reported["displayName"]);
            if (HarviaJson.Bool(reported["active"]) is bool active)
                Active = active;
            if (HarviaJson.Bool(reported["light"]) is bool light)
                Light = light;
            if (HarviaJson.Int(reported["targetTemp"]) is int target)
                TargetTemperature = target;
            if (HarviaJson.Int(reported["heatUpTime"]) is int heatUp)
                HeatUpMinutes = heatUp;
            if (HarviaJson.Int(reported["statusCodes"]) is int codes)
                StatusCodes = codes;
            if (reported.ContainsKey("timedStart"))
                TimedStart = HarviaJson.Text(reported["timedStart"]);
            Seen(at);
        }

        /// <summary>Merges telemetry: temperature, humidity, remaining time and the heater flag.</summary>
        public void MergeTelemetry(JsonObject data, DateTimeOffset? at)
        {
            if (HarviaJson.Number(data["temperature"]) is double temperature)
                Temperature = temperature;
            if (HarviaJson.Number(data["humidity"]) is double humidity)
                Humidity = humidity;
            if (HarviaJson.Int(data["remainingTime"]) is int remaining)
                RemainingMinutes = remaining;
            if (HarviaJson.Bool(data["heatOn"]) is bool heatOn)
                HeatOn = heatOn;
            if (TargetTemperature == null && HarviaJson.Int(data["targetTemp"]) is int target)
                TargetTemperature = target;
            Seen(at);
        }

        private void Seen(DateTimeOffset? at)
        {
            if (at is DateTimeOffset time && (LastSeen == null || time > LastSeen))
                LastSeen = time;
        }
    }

    /// <summary>Reads the loosely typed values of the Harvia cloud: numbers as strings, flags as 0/1.</summary>
    internal static class HarviaJson
    {
        // By kind, not TryGetValue: a value built in code (an int) doesn't convert to double, a parsed one does.
        public static string? Text(JsonNode? node) => node is JsonValue value
            ? value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString()
            : null;

        public static double? Number(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            var text = value.GetValueKind() switch
            {
                JsonValueKind.Number => value.ToJsonString(),
                JsonValueKind.String => value.GetValue<string>(),
                _ => null
            };
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        public static int? Int(JsonNode? node) => Number(node) is double number ? (int)Math.Round(number) : null;

        public static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            return value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => Number(node) is double number ? number != 0 : null
            };
        }

        /// <summary>A JSON object, or one encoded as a string (AppSync's AWSJSON).</summary>
        public static JsonObject? Object(JsonNode? node)
        {
            if (node is JsonObject json)
                return json;
            if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is string text && !string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    return JsonNode.Parse(text) as JsonObject;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>A Unix time in seconds or milliseconds, as a number or a string.</summary>
        public static DateTimeOffset? Time(JsonNode? node)
        {
            if (Number(node) is not double value || value <= 0)
                return null;
            try
            {
                return value > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : DateTimeOffset.FromUnixTimeSeconds((long)value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    /// <summary>A sauna session in progress, kept across restarts.</summary>
    internal sealed class HarviaOpenSession
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? ReadyAt { get; set; }
        public double? MaxTemperature { get; set; }
        public int? TargetTemperature { get; set; }
        public double Energy { get; set; }
    }

    /// <summary>A sauna's session and day state, kept in a file so a restart keeps the open session and today's totals.</summary>
    internal sealed class HarviaSessionState
    {
        /// <summary>The local day (yyyy-MM-dd) the totals are for.</summary>
        public string? Day { get; set; }
        public int SessionsToday { get; set; }
        public int BathingMinutesToday { get; set; }
        public double EnergyToday { get; set; }
        public HarviaOpenSession? Open { get; set; }
        public DateTimeOffset? LastUpdate { get; set; }
        public bool LastHeating { get; set; }
    }

    /// <summary>A finished session.</summary>
    internal sealed record HarviaSession(DateTimeOffset StartedAt, DateTimeOffset? ReadyAt, DateTimeOffset EndedAt, int DurationMinutes,
        int? HeatUpMinutes, double? MaxTemperature, int? TargetTemperature, double? Energy);

    /// <summary>What one update changed: the session events in order and a finished session.</summary>
    internal sealed record HarviaSessionUpdate(bool Changed, IReadOnlyList<string> Events, HarviaSession? Ended);

    /// <summary>
    /// Follows a sauna's sessions: one starts when it is switched on and ends when it is switched off; it is ready when
    /// the target is reached (or the configured ready temperature). The energy is an estimate: the heater's rated power
    /// while it heats up, integrated between updates, at most <see cref="MaxGap"/> per gap so a lost update doesn't
    /// count hours.
    /// </summary>
    internal static class HarviaSessions
    {
        public const string Started = "started";
        public const string ReadyEvent = "ready";
        public const string Ended = "ended";

        public static readonly string[] Events = [Started, ReadyEvent, Ended];

        public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);

        public static HarviaSessionUpdate Update(HarviaSessionState state, HarviaSauna sauna, DateTimeOffset now, string day,
            double heaterPowerKw, double readyTemperature)
        {
            var events = new List<string>();
            var changed = false;
            HarviaSession? ended = null;

            if (state.Day != day)
            {
                state.Day = day;
                state.SessionsToday = 0;
                state.BathingMinutesToday = 0;
                state.EnergyToday = 0;
                changed = true;
            }

            if (heaterPowerKw > 0 && state.LastHeating && state.LastUpdate is DateTimeOffset last && now > last)
            {
                var gap = now - last < MaxGap ? now - last : MaxGap;
                var energy = heaterPowerKw * gap.TotalHours;
                state.EnergyToday += energy;
                if (state.Open != null)
                    state.Open.Energy += energy;
                changed = true;
            }

            if (sauna.Active == true && state.Open == null)
            {
                state.Open = new HarviaOpenSession { StartedAt = now, TargetTemperature = sauna.TargetTemperature };
                events.Add(Started);
                changed = true;
            }

            if (state.Open is HarviaOpenSession open)
            {
                if (sauna.Temperature is double temperature && (open.MaxTemperature == null || temperature > open.MaxTemperature))
                {
                    open.MaxTemperature = temperature;
                    changed = true;
                }
                if (sauna.TargetTemperature != null && open.TargetTemperature != sauna.TargetTemperature)
                {
                    open.TargetTemperature = sauna.TargetTemperature;
                    changed = true;
                }
                if (sauna.Active == true && open.ReadyAt == null && IsReady(sauna, readyTemperature))
                {
                    open.ReadyAt = now;
                    events.Add(ReadyEvent);
                    changed = true;
                }
                if (sauna.Active == false)
                {
                    var minutes = (int)Math.Round((now - open.StartedAt).TotalMinutes);
                    ended = new HarviaSession(open.StartedAt, open.ReadyAt, now, minutes,
                        open.ReadyAt is DateTimeOffset ready ? (int)Math.Round((ready - open.StartedAt).TotalMinutes) : null,
                        open.MaxTemperature, open.TargetTemperature, heaterPowerKw > 0 ? Math.Round(open.Energy, 3) : null);
                    state.SessionsToday++;
                    state.BathingMinutesToday += minutes;
                    state.Open = null;
                    events.Add(Ended);
                    changed = true;
                }
            }

            var heating = sauna.Heating == true;
            if (heating != state.LastHeating)
                changed = true;
            state.LastHeating = heating;
            state.LastUpdate = now;
            return new HarviaSessionUpdate(changed, events, ended);
        }

        /// <summary>Ready: the configured temperature reached, or else the target (the panel's bit, or the temperature).</summary>
        public static bool IsReady(HarviaSauna sauna, double readyTemperature)
        {
            if (readyTemperature > 0)
                return sauna.Temperature >= readyTemperature;
            return sauna.TargetReached == true || (sauna.Temperature is double temperature && sauna.TargetTemperature is int target && temperature >= target);
        }
    }

    /// <summary>
    /// The cabin temperature's trend over the last <see cref="Window"/>, for an estimate of when the sauna is ready.
    /// Below <see cref="MinimumRate"/> the estimate is unknown.
    /// </summary>
    internal sealed class HarviaTrend
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        /// <summary>°C per minute.</summary>
        public const double MinimumRate = 0.05;

        private readonly List<(DateTimeOffset At, double Temperature)> _samples = [];

        public void Add(DateTimeOffset at, double temperature)
        {
            if (_samples.Count > 0 && _samples[^1].At >= at)
            {
                if (_samples[^1].At == at)
                    _samples[^1] = (at, temperature);
                return;
            }
            _samples.Add((at, temperature));
            _samples.RemoveAll(x => at - x.At > Window);
        }

        public void Clear() => _samples.Clear();

        /// <summary>The heating rate in °C per minute, from the oldest and newest sample at least a minute apart.</summary>
        public double? Rate
        {
            get
            {
                if (_samples.Count < 2)
                    return null;
                var (firstAt, first) = _samples[0];
                var (lastAt, last) = _samples[^1];
                var minutes = (lastAt - firstAt).TotalMinutes;
                return minutes < 1 ? null : (last - first) / minutes;
            }
        }

        /// <summary>Minutes until <paramref name="target"/> is reached, rounded up, or null when unknown.</summary>
        public int? MinutesTo(double target, double current)
        {
            if (current >= target)
                return 0;
            if (Rate is not double rate || rate < MinimumRate)
                return null;
            return (int)Math.Ceiling((target - current) / rate);
        }
    }
}
