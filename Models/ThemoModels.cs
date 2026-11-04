using System.Globalization;
using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>One Themo thermostat with the state the last poll read.</summary>
    internal sealed class ThemoThermostat
    {
        public int Id { get; set; }
        public int EnvironmentId { get; set; }
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Software { get; set; }

        /// <summary>The temperature of the sensor the thermostat regulates by (Info).</summary>
        public double? Temperature { get; set; }
        public double? RoomTemperature { get; set; }
        public double? FloorTemperature { get; set; }
        public double? OutsideTemperature { get; set; }
        /// <summary>The manual mode's target temperature (MT).</summary>
        public double? SetTemperature { get; set; }
        /// <summary>The mode as Themo names it: Off, Manual or SLS.</summary>
        public string? Mode { get; set; }
        public bool? Heating { get; set; }
        public bool? Connected { get; set; }
        public bool? Lights { get; set; }
        public int? Lock { get; set; }
        public bool? FrostProtection { get; set; }
        public double? FrostTemperature { get; set; }
        /// <summary>The rated power of the heating load in kW, from MP.</summary>
        public double? MaxPower { get; set; }

        /// <summary>The name of the active minimum temperature schedule.</summary>
        public string? Schedule { get; set; }
        public List<ThemoSchedule> Schedules { get; set; } = [];

        /// <summary>The finished hours of the last 48 hours, in time order.</summary>
        public List<ThemoEnergyHour> Hours { get; set; } = [];
        public double? EnergyToday { get; set; }
        public double? HeatingShareToday { get; set; }

        public ThemoThermostat Copy()
        {
            var copy = (ThemoThermostat)MemberwiseClone();
            copy.Schedules = Schedules.ToList();
            copy.Hours = Hours.ToList();
            return copy;
        }
    }

    internal sealed record ThemoSchedule(int Id, string Name, bool Active);

    /// <summary>One command field and its value (text, int or double) for the thermostat's message endpoint.</summary>
    internal sealed record ThemoMessage(string Field, object Value);

    internal sealed record ThemoEnergyHour(DateTimeOffset Start, double Energy);

    /// <summary>The thermostat's modes and lock states, and their Matter SystemMode.</summary>
    internal static class ThemoModes
    {
        public const string Off = "off";
        public const string Manual = "manual";
        public const string Sls = "sls";

        public const int MatterOff = 0;
        public const int MatterAuto = 1;
        public const int MatterHeat = 4;

        public static readonly IReadOnlyList<string> All = [Off, Manual, Sls];

        /// <summary>The lock states by their number: 0 off, 1 on, 2 hard lock, 3 manual mode timer boost.</summary>
        public static readonly IReadOnlyList<string> Locks = ["off", "on", "hard", "boost"];

        /// <summary>The mode's name in Themo's API.</summary>
        public static string ToApi(string mode) => mode switch
        {
            Off => "Off",
            Sls => "SLS",
            _ => "Manual"
        };

        public static string? FromApi(string? mode) => mode?.Trim().ToLowerInvariant() switch
        {
            "off" => Off,
            "manual" => Manual,
            "sls" => Sls,
            _ => null
        };

        /// <summary>Off is Matter's off, Manual heat, and SLS (Themo's schedule and price optimization) auto.</summary>
        public static int? ToMatter(string? mode) => mode switch
        {
            Off => MatterOff,
            Manual => MatterHeat,
            Sls => MatterAuto,
            _ => null
        };

        public static string FromMatter(int systemMode) => systemMode switch
        {
            MatterOff => Off,
            MatterHeat => Manual,
            MatterAuto => Sls,
            _ => throw new ArgumentOutOfRangeException(nameof(systemMode), systemMode, "The thermostat mode must be 0 (off), 1 (auto: SLS) or 4 (heat: manual).")
        };

        public static string? LockName(int? state) => state is int s && s >= 0 && s < Locks.Count ? Locks[s] : null;
    }

    /// <summary>Themo's hourly device data and the rated power.</summary>
    internal static class ThemoEnergy
    {
        public const string Series = "Price data";
        public const string Column = "Consumption";

        /// <summary>
        /// The hourly consumption in kWh from the device data's "Price data" series, whose "Consumption" column is in
        /// MWh. An hour without a value (the current one) is left out.
        /// </summary>
        public static List<ThemoEnergyHour> Parse(JsonNode? data)
        {
            var series = (data as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => (string?)x["Name"] == Series);
            var definitions = (series?["TimeSeriesDefinitions"] as JsonArray)?.ToList() ?? [];
            var column = definitions.FindIndex(x => (string?)x?["Name"] == Column);
            if (series == null || column < 0)
                return [];

            var hours = new List<ThemoEnergyHour>();
            foreach (var point in (series["TimeSeries"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var values = point["Values"] as JsonArray;
                if (values == null || column >= values.Count || Number(values[column]) is not double megawattHours || Number(point["Time"]) is not double time)
                    continue;
                hours.Add(new ThemoEnergyHour(DateTimeOffset.FromUnixTimeMilliseconds((long)time), Math.Round(megawattHours * 1000, 6)));
            }
            return hours.GroupBy(x => x.Start).Select(x => x.Last()).OrderBy(x => x.Start).ToList();
        }

        /// <summary>
        /// MP in kW. Themo doesn't document its unit; the commands take power in MW, so a value up to 1 is taken as MW,
        /// up to 100 as kW and a larger one as W.
        /// </summary>
        public static double? MaxPowerKw(double? maxPower) => maxPower switch
        {
            null or <= 0 => null,
            <= 1 => maxPower.Value * 1000,
            <= 100 => maxPower.Value,
            _ => maxPower.Value / 1000
        };

        internal static double? Number(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<double>(out var number))
                return number;
            if (value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
            return null;
        }
    }

    /// <summary>The last hour whose energy was reported, so a restart reports the hours it missed.</summary>
    internal sealed class ThemoEnergyState
    {
        public DateTimeOffset LastReportedHour { get; set; }
    }
}
