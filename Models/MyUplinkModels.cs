using System.Globalization;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>One myUplink parameter (a "point") of a heat pump: its value and what may be written to it.</summary>
    internal sealed class MyUplinkPoint
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public bool Writable { get; set; }
        public double? Value { get; set; }
        /// <summary>The value as myUplink formats it, for a value that isn't a number.</summary>
        public string? Text { get; set; }
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public double? Step { get; set; }
        public List<MyUplinkEnumValue> Enum { get; set; } = [];

        /// <summary>The enum text of the value, for an enum parameter.</summary>
        public string? EnumText => Enum.Count == 0 || Value == null ? null :
            Enum.FirstOrDefault(x => double.TryParse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v == Value)?.Text
                ?? Enum.FirstOrDefault(x => x.Value == MyUplinkSetpoint.Format(Value.Value))?.Text;

        public MyUplinkPoint Copy()
        {
            var copy = (MyUplinkPoint)MemberwiseClone();
            copy.Enum = Enum.ToList();
            return copy;
        }
    }

    internal sealed record MyUplinkEnumValue(string Value, string Text);

    /// <summary>One heat pump in a myUplink system, with the state the last poll read.</summary>
    internal sealed class MyUplinkPump
    {
        public string Id { get; set; } = "";
        public string SystemId { get; set; } = "";
        public string Name { get; set; } = "";
        public string ProductName { get; set; } = "";
        public bool? Connected { get; set; }
        public bool? HasAlarm { get; set; }
        public int? ActiveAlarms { get; set; }
        public string? AlarmText { get; set; }
        public string? SmartHomeMode { get; set; }
        public bool? PremiumManage { get; set; }
        public double? EnergyToday { get; set; }

        /// <summary>Every parameter the device has, read once at discovery, by id; commands are checked against these.</summary>
        public Dictionary<string, MyUplinkPoint> Points { get; set; } = [];

        /// <summary>The curated keys this device has, with their parameter ids.</summary>
        public Dictionary<string, string> Known { get; set; } = [];

        /// <summary>The configured extra parameter ids this device has.</summary>
        public List<string> Extras { get; set; } = [];

        /// <summary>The parameter ids each poll reads.</summary>
        public IEnumerable<string> PolledIds => Known.Values.Concat(Extras).Distinct();

        public MyUplinkPoint? Point(string key) =>
            Known.TryGetValue(key, out var id) && Points.TryGetValue(id, out var point) ? point : null;

        public MyUplinkPump Copy()
        {
            var copy = (MyUplinkPump)MemberwiseClone();
            copy.Points = Points.ToDictionary(x => x.Key, x => x.Value.Copy());
            copy.Known = new Dictionary<string, string>(Known);
            copy.Extras = Extras.ToList();
            return copy;
        }
    }

    /// <summary>
    /// A curated parameter: its report key and the NIBE parameter ids it may have; the first id the device has wins.
    /// The ids are NIBE's register ids, which myUplink uses for the F-series (F750); other models and firmware can
    /// differ, so a missing id is skipped and <c>extraParameters</c> adds others.
    /// </summary>
    internal sealed record MyUplinkKnownPoint(string Key, string Name, string[] Ids, string? Unit = null, string? Semantic = null, bool Command = false);

    internal static class MyUplinkCatalog
    {
        public const string HeatingOffset = "heatingOffset";
        public const string ExhaustAirTemperature = "exhaustAirTemperature";
        public const string EnergyHeating = "energyHeating";
        public const string EnergyHotWater = "energyHotWater";
        public const string HotWaterComfortMode = "hotWaterComfortMode";
        public const string HotWaterBoost = "hotWaterBoost";
        public const string VentilationSpeed = "ventilationSpeed";

        public static readonly IReadOnlyList<MyUplinkKnownPoint> Points =
        [
            new("outdoorTemperature", "outdoor temperature", ["40004"], "°C", ValueSemantics.Temperature),
            new("supplyTemperature", "supply temperature", ["40008"], "°C", ValueSemantics.Temperature),
            new("returnTemperature", "return temperature", ["40012"], "°C", ValueSemantics.Temperature),
            new("hotWaterTop", "hot water top temperature", ["40013"], "°C", ValueSemantics.Temperature),
            new("hotWaterCharging", "hot water charging temperature", ["40014"], "°C", ValueSemantics.Temperature),
            new(ExhaustAirTemperature, "exhaust air temperature", ["40025"], "°C", ValueSemantics.Temperature),
            new("extractAirTemperature", "extract air temperature", ["40026"], "°C", ValueSemantics.Temperature),
            new("calculatedSupplyTemperature", "calculated supply temperature", ["43009"], "°C", ValueSemantics.Temperature),
            new("degreeMinutes", "degree minutes", ["43005", "40940"], "DM"),
            new("compressorFrequency", "compressor frequency", ["43136"], "Hz"),
            new("compressorStarts", "compressor starts", ["43416"]),
            new("compressorHours", "compressor operating time", ["43420"], "h"),
            new("hotWaterCompressorHours", "compressor operating time for hot water", ["43424"], "h"),
            new("additionalHeatPower", "additional heat power", ["43084"], "kW", ValueSemantics.Power),
            new("fanSpeed", "fan speed", ["10001"], "%"),
            // The offset's id comes from the offsetParameter option.
            new(HeatingOffset, "heating offset", [], Command: true),
            new(HotWaterComfortMode, "hot water comfort mode", ["47041"], Command: true),
            new(HotWaterBoost, "hot water boost", ["48132"], Command: true),
            new(VentilationSpeed, "ventilation speed", ["47260"], Command: true),
            // Heat meters: the heat the pump produced, including the additional heat; not the electricity it used.
            new(EnergyHeating, "heat produced for heating", ["44300", "44308"], "kWh", ValueSemantics.Energy),
            new(EnergyHotWater, "heat produced for hot water", ["44298", "44306"], "kWh", ValueSemantics.Energy),
        ];

        /// <summary>Maps each curated key to the first of its ids that <paramref name="points"/> has.</summary>
        public static Dictionary<string, string> Match(IReadOnlyDictionary<string, MyUplinkPoint> points, string offsetParameter)
        {
            var known = new Dictionary<string, string>();
            foreach (var entry in Points)
            {
                string[] ids = entry.Key == HeatingOffset ? [offsetParameter] : entry.Ids;
                var id = ids.FirstOrDefault(points.ContainsKey);
                if (id != null)
                    known[entry.Key] = id;
            }
            return known;
        }
    }

    /// <summary>
    /// The virtual setpoint: a heat pump without a room sensor has no target temperature, only the heating curve's
    /// offset. A target becomes the offset <c>round((target - base) / degreesPerStep)</c>; the offset reads back as
    /// <c>base + offset * degreesPerStep</c>.
    /// </summary>
    internal static class MyUplinkSetpoint
    {
        public static double ToOffset(double target, double baseTemperature, double degreesPerStep, double step = 1) =>
            Math.Round(Math.Round((target - baseTemperature) / degreesPerStep / step, MidpointRounding.AwayFromZero) * step, 3);

        public static double ToTemperature(double offset, double baseTemperature, double degreesPerStep) =>
            Math.Round(baseTemperature + offset * degreesPerStep, 2);

        public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>The produced heat at the first poll of a day, from which <c>energyToday</c> counts.</summary>
    internal sealed class MyUplinkEnergyBaseline
    {
        public string Day { get; set; } = "";
        public double Total { get; set; }
    }
}
