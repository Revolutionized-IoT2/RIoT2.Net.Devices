namespace RIoT2.Net.Devices.Models
{
    /// <summary>One connector of a DEFA Power charge point, with the state the last polls read.</summary>
    internal sealed class DefaPowerConnector
    {
        /// <summary>The connector's id; the CloudCharge API's connector endpoints take it.</summary>
        public string Id { get; set; } = "";

        /// <summary>The connector's key in the charge point's alias map; starting and stopping take it.</summary>
        public string Alias { get; set; } = "";

        public string ChargePointId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Vendor { get; set; }
        public string? Model { get; set; }
        public string? Firmware { get; set; }
        public bool EcoModeCapable { get; set; }
        public bool ManualSchedulesCapable { get; set; }

        /// <summary>The charge current limit in amperes (maxProfileCurrent).</summary>
        public int? CurrentLimit { get; set; }

        /// <summary>The OCPP status, camelCase: available, preparing, charging, suspendedEv, ...</summary>
        public string? Status { get; set; }

        /// <summary>The charging state, camelCase: idle, evConnected, charging, suspendedEv, suspendedEvse.</summary>
        public string? ChargingState { get; set; }

        /// <summary>The lifetime energy meter in kWh.</summary>
        public double? Meter { get; set; }

        /// <summary>The energy of the current charging transaction in kWh.</summary>
        public double? TransactionEnergy { get; set; }

        /// <summary>The charging power in kW.</summary>
        public double? Power { get; set; }

        public bool? Online { get; set; }
        public string? ErrorCode { get; set; }

        public bool? EcoMode { get; set; }
        public int? EcoHoursToCharge { get; set; }
        public bool? ManualSchedules { get; set; }

        /// <summary>What pauses smart charging: none, ecoMode or manualSchedules.</summary>
        public string? PausedBy { get; set; }
        public DateTimeOffset? PausedUntil { get; set; }
        public bool? ScheduleOverridden { get; set; }

        /// <summary>The current limits the charger accepts, in amperes.</summary>
        public List<int> CurrentAlternatives { get; set; } = [];

        public bool Charging => ChargingState == DefaPowerStates.Charging;

        public DefaPowerConnector Copy()
        {
            var copy = (DefaPowerConnector)MemberwiseClone();
            copy.CurrentAlternatives = CurrentAlternatives.ToList();
            return copy;
        }
    }

    /// <summary>The CloudCharge API's states, camelCase for the reports.</summary>
    internal static class DefaPowerStates
    {
        public const string Idle = "idle";
        public const string EvConnected = "evConnected";
        public const string Charging = "charging";
        public const string SuspendedEv = "suspendedEv";
        public const string SuspendedEvse = "suspendedEvse";

        public const string PausedByNone = "none";
        public const string PausedByEcoMode = "ecoMode";
        public const string PausedByManualSchedules = "manualSchedules";

        public static readonly IReadOnlyList<string> ChargingStates = [Idle, EvConnected, Charging, SuspendedEv, SuspendedEvse];

        public static readonly IReadOnlyList<string> PausedBy = [PausedByNone, PausedByEcoMode, PausedByManualSchedules];

        public static readonly IReadOnlyList<string> Statuses =
            ["available", "preparing", "charging", "suspendedEv", "suspendedEvse", "finishing", "reserved", "unavailable", "faulted",
             "occupied", "restarting", "facilityFinishing"];

        /// <summary>
        /// An API state in camelCase: <c>SuspendedEVSE</c> is <c>suspendedEvse</c>, <c>EVConnected</c> <c>evConnected</c>,
        /// <c>UNRECOGNIZED</c> <c>unrecognized</c>.
        /// </summary>
        public static string? CamelCase(string? state)
        {
            var text = state?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;
            if (text.Contains('_', StringComparison.Ordinal))
            {
                var words = text.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
                return words.Length == 0 ? null : words[0] + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
            }
            if (text.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                return text.ToLowerInvariant();
            text = text.Replace("EVSE", "Evse", StringComparison.Ordinal).Replace("EV", "Ev", StringComparison.Ordinal);
            return char.ToLowerInvariant(text[0]) + text[1..];
        }

        /// <summary>Whether a car is plugged in: any charging state but idle, or without one, an OCPP status of a transaction.</summary>
        public static bool? Connected(string? chargingState, string? status)
        {
            if (chargingState is EvConnected or Charging or SuspendedEv or SuspendedEvse)
                return true;
            if (chargingState == Idle)
                return false;
            return status switch
            {
                "preparing" or "charging" or "suspendedEv" or "suspendedEvse" or "finishing" or "occupied" => true,
                "available" or "unavailable" or "faulted" or "reserved" => false,
                _ => null
            };
        }
    }

    /// <summary>The energy of one quarter-hour, in kWh.</summary>
    internal sealed class DefaPowerQuarter
    {
        public DateTimeOffset Start { get; set; }
        public double Energy { get; set; }
    }

    /// <summary>A charging session that is still open: a car plugged in.</summary>
    internal sealed class DefaPowerOpenSession
    {
        public DateTimeOffset Start { get; set; }
        public double StartMeter { get; set; }
    }

    /// <summary>A finished charging session.</summary>
    internal sealed record DefaPowerSession(DateTimeOffset Start, DateTimeOffset End, double Energy, double? Cost);

    /// <summary>One electricity price slot, in the price series' unit.</summary>
    internal sealed class DefaPowerPrice
    {
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
        public double Price { get; set; }
    }

    /// <summary>The energy accounting of one connector, kept in the state file so a restart continues it.</summary>
    internal sealed class DefaPowerConnectorState
    {
        /// <summary>The first meter sample: quarters and hours that start before it are incomplete and not reported.</summary>
        public DateTimeOffset? Since { get; set; }
        public DateTimeOffset? SampleTime { get; set; }
        public double? SampleMeter { get; set; }

        /// <summary>The quarters of the last two days, the current one included, in time order.</summary>
        public List<DefaPowerQuarter> Quarters { get; set; } = [];
        public DateTimeOffset? LastReportedQuarter { get; set; }
        public DateTimeOffset? LastReportedHour { get; set; }
        public DateTimeOffset? LastCostedQuarter { get; set; }
        public DefaPowerOpenSession? Session { get; set; }
    }

    /// <summary>The device's state file: the connectors' energy accounting and the electricity prices.</summary>
    internal sealed class DefaPowerState
    {
        public Dictionary<string, DefaPowerConnectorState> Connectors { get; set; } = [];
        public string? PriceUnit { get; set; }
        public List<DefaPowerPrice> Prices { get; set; } = [];
    }

    /// <summary>The CloudCharge login, kept in the token file.</summary>
    internal sealed class DefaPowerLogin
    {
        public string UserId { get; set; } = "";
        public string Token { get; set; } = "";
    }
}
