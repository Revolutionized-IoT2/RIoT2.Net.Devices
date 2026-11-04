using System.Globalization;
using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>
    /// One air-to-air unit as the <see cref="Catalog.MelCloud"/> device sees it, whichever MELCloud
    /// service it came from. A null value is one the unit or the service doesn't report.
    /// </summary>
    public sealed class MelCloudUnit
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The classic MELCloud building id, which its device calls need; empty for MELCloud Home.</summary>
        public string BuildingId { get; set; } = "";

        public bool? Power { get; set; }
        /// <summary>One of <see cref="MelCloudModes"/>' values, or null for a mode the device doesn't know.</summary>
        public string? Mode { get; set; }
        public double? SetTemperature { get; set; }
        public double? RoomTemperature { get; set; }
        /// <summary><c>auto</c> or <c>1</c>-<c>5</c>.</summary>
        public string? FanSpeed { get; set; }
        /// <summary><c>auto</c>, <c>1</c>-<c>5</c> (top to bottom) or <c>swing</c>.</summary>
        public string? VaneVertical { get; set; }
        /// <summary><c>auto</c>, <c>1</c>-<c>5</c> (left to right), <c>split</c> or <c>swing</c>.</summary>
        public string? VaneHorizontal { get; set; }
        public bool? Offline { get; set; }
        /// <summary>The classic meter reading in kWh, which only grows.</summary>
        public double? EnergyConsumed { get; set; }
        /// <summary>MELCloud Home's consumption since local midnight, in kWh.</summary>
        public double? EnergyToday { get; set; }

        public double MinTemperature { get; set; } = 10;
        public double MaxTemperature { get; set; } = 31;
        public double TemperatureStep { get; set; } = 0.5;

        public MelCloudUnit Copy() => (MelCloudUnit)MemberwiseClone();

        /// <summary>The Matter Thermostat SystemMode for the unit's power and mode, or null when it has neither.</summary>
        public int? ThermostatMode => Power switch
        {
            false => MelCloudModes.MatterOff,
            true => MelCloudModes.ToMatter(Mode),
            null => null
        };

        /// <summary>Applies a change the unit accepted, so the next reports show it before the next poll.</summary>
        public void Apply(MelCloudChange change)
        {
            Power = change.Power ?? Power;
            Mode = change.Mode ?? Mode;
            SetTemperature = change.SetTemperature ?? SetTemperature;
            FanSpeed = change.FanSpeed ?? FanSpeed;
            VaneVertical = change.VaneVertical ?? VaneVertical;
            VaneHorizontal = change.VaneHorizontal ?? VaneHorizontal;
        }
    }

    /// <summary>The settings one command changes; null members are left as they are.</summary>
    public sealed class MelCloudChange
    {
        public bool? Power { get; set; }
        public string? Mode { get; set; }
        public double? SetTemperature { get; set; }
        public string? FanSpeed { get; set; }
        public string? VaneVertical { get; set; }
        public string? VaneHorizontal { get; set; }
    }

    /// <summary>The device's operation mode, fan and vane names, and the Matter SystemMode they map to.</summary>
    public static class MelCloudModes
    {
        public const string Heat = "heat";
        public const string Dry = "dry";
        public const string Cool = "cool";
        public const string Fan = "fan";
        public const string Auto = "auto";

        public static readonly IReadOnlyList<string> All = [Heat, Dry, Cool, Fan, Auto];
        public static readonly IReadOnlyList<string> FanSpeeds = ["auto", "1", "2", "3", "4", "5"];
        public static readonly IReadOnlyList<string> VerticalVanes = ["auto", "1", "2", "3", "4", "5", "swing"];
        public static readonly IReadOnlyList<string> HorizontalVanes = ["auto", "1", "2", "3", "4", "5", "split", "swing"];

        // Matter Thermostat SystemMode values (0x0201, attribute 0x001C).
        public const int MatterOff = 0;
        public const int MatterAuto = 1;
        public const int MatterCool = 3;
        public const int MatterHeat = 4;
        public const int MatterFanOnly = 7;
        public const int MatterDry = 8;

        public static int? ToMatter(string? mode) => mode switch
        {
            Heat => MatterHeat,
            Cool => MatterCool,
            Dry => MatterDry,
            Fan => MatterFanOnly,
            Auto => MatterAuto,
            _ => null
        };

        /// <summary>The change for a Matter SystemMode: off powers the unit off, any other mode powers it on in that mode.</summary>
        public static MelCloudChange FromMatter(int systemMode) => systemMode switch
        {
            MatterOff => new MelCloudChange { Power = false },
            MatterAuto => new MelCloudChange { Power = true, Mode = Auto },
            MatterCool => new MelCloudChange { Power = true, Mode = Cool },
            MatterHeat => new MelCloudChange { Power = true, Mode = Heat },
            MatterFanOnly => new MelCloudChange { Power = true, Mode = Fan },
            MatterDry => new MelCloudChange { Power = true, Mode = Dry },
            _ => throw new ArgumentOutOfRangeException(nameof(systemMode), systemMode,
                "The thermostat mode must be 0 (off), 1 (auto), 3 (cool), 4 (heat), 7 (fan) or 8 (dry).")
        };

        /// <summary>The name in <paramref name="names"/> that <paramref name="value"/> means, ignoring case; null for none.</summary>
        public static string? Find(IReadOnlyList<string> names, string? value)
        {
            var text = value?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;
            // A whole-number value such as 3.0 (a numeric command) names the speed or position 3.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == Math.Floor(number))
                text = ((int)number).ToString(CultureInfo.InvariantCulture);
            return names.FirstOrDefault(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The MELCloud login a device keeps in <c>Data/melcloud-&lt;cloud&gt;-&lt;account&gt;.json</c>, so a restart
    /// doesn't log in again: MELCloud limits logins.
    /// </summary>
    public sealed class MelCloudSession
    {
        /// <summary>Classic MELCloud's context key (X-MitsContextKey).</summary>
        [JsonPropertyName("contextKey")]
        public string? ContextKey { get; set; }

        /// <summary>MELCloud Home's OAuth access token.</summary>
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        /// <summary>MELCloud Home's OAuth refresh token; it changes on every refresh.</summary>
        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        /// <summary>When the access token expires.</summary>
        [JsonPropertyName("expiresAt")]
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
