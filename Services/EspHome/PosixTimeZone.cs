using System.Globalization;
using System.Text;

namespace RIoT2.Net.Devices.Services.EspHome
{
    /// <summary>
    /// The local time zone as a POSIX TZ rule (for example <c>&lt;+02&gt;-2&lt;+03&gt;,M3.5.0/3,M10.5.0/4</c>), which the
    /// ESPHome API's <c>GetTimeResponse.timezone</c> carries: Home Assistant sends the rule, not the zone's name.
    /// </summary>
    public static class PosixTimeZone
    {
        private const string ZoneDirectory = "/usr/share/zoneinfo";

        /// <summary>The local zone's rule; empty when it can't be told.</summary>
        public static string Local()
        {
            var zone = TimeZoneInfo.Local;
            if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime)
                return "UTC0";
            return FromZoneFile(zone.Id) ?? FromRules(zone, DateTimeOffset.UtcNow) ?? "";
        }

        // The rule at the end of the zone's TZif file (version 2 and later), as aioesphomeapi reads it.
        private static string? FromZoneFile(string id)
        {
            if (id.Length is 0 or > 64 || id.Contains("..", StringComparison.Ordinal) || id.StartsWith('/') ||
                !id.All(x => char.IsAsciiLetterOrDigit(x) || x is '/' or '_' or '-' or '+'))
                return null;
            try
            {
                var path = Path.Combine(Environment.GetEnvironmentVariable("TZDIR") ?? ZoneDirectory, id);
                var info = new FileInfo(path);
                return info.Exists && info.Length < 1 << 20 ? FromTzif(File.ReadAllBytes(path)) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The rule in a TZif file's footer: the text between its last two newlines.</summary>
        public static string? FromTzif(byte[] file)
        {
            if (file.Length < 6 || !file.AsSpan(0, 4).SequenceEqual("TZif"u8) || file[4] < (byte)'2' || file[^1] != '\n')
                return null;
            var start = Array.LastIndexOf(file, (byte)'\n', file.Length - 2);
            if (start < 0)
                return null;
            var rule = Encoding.ASCII.GetString(file, start + 1, file.Length - start - 2);
            return rule.Length is > 0 and <= 64 && rule.All(x => x is >= ' ' and <= '~') ? rule : null;
        }

        /// <summary>
        /// The rule built from the zone's adjustment rule in force at <paramref name="now"/>; null when that rule has
        /// fixed dates, which a POSIX rule can't say this way.
        /// </summary>
        public static string? FromRules(TimeZoneInfo zone, DateTimeOffset now)
        {
            var date = now.UtcDateTime.Date;
            var rule = zone.GetAdjustmentRules().FirstOrDefault(x => x.DateStart <= date && x.DateEnd >= date);
            var standard = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
            var text = Name(standard) + Offset(standard);
            if (rule == null || rule.DaylightDelta == TimeSpan.Zero)
                return text;
            if (rule.DaylightTransitionStart.IsFixedDateRule || rule.DaylightTransitionEnd.IsFixedDateRule)
                return null;
            var daylight = standard + rule.DaylightDelta;
            return $"{text}{Name(daylight)}{Offset(daylight)},{Transition(rule.DaylightTransitionStart)},{Transition(rule.DaylightTransitionEnd)}";
        }

        // <+02>, <-0330>: a numeric name, as tzdata uses for zones without an abbreviation.
        private static string Name(TimeSpan offset)
        {
            var sign = offset < TimeSpan.Zero ? '-' : '+';
            var abs = offset.Duration();
            return abs.Minutes == 0
                ? string.Create(CultureInfo.InvariantCulture, $"<{sign}{abs.Hours:00}>")
                : string.Create(CultureInfo.InvariantCulture, $"<{sign}{abs.Hours:00}{abs.Minutes:00}>");
        }

        // POSIX offsets are west of Greenwich: UTC+2 is "-2".
        private static string Offset(TimeSpan offset)
        {
            var sign = offset > TimeSpan.Zero ? "-" : "";
            var abs = offset.Duration();
            return abs.Minutes == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours}")
                : string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours}:{abs.Minutes:00}");
        }

        // Mm.w.d/time: week 5 is the last, day 0 is Sunday; 02:00 is the default time.
        private static string Transition(TimeZoneInfo.TransitionTime transition)
        {
            var at = transition.TimeOfDay.TimeOfDay;
            var text = string.Create(CultureInfo.InvariantCulture, $"M{transition.Month}.{transition.Week}.{(int)transition.DayOfWeek}");
            if (at == TimeSpan.FromHours(2))
                return text;
            return at.Minutes == 0 && at.Seconds == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{text}/{at.Hours}")
                : string.Create(CultureInfo.InvariantCulture, $"{text}/{at.Hours}:{at.Minutes:00}:{at.Seconds:00}");
        }
    }
}
