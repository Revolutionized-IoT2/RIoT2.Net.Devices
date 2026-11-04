using System.Globalization;
using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>One Miele appliance with the state Miele last sent.</summary>
    internal sealed class MieleAppliance
    {
        /// <summary>The fabrication number, Miele's id of the appliance.</summary>
        public string Id { get; set; } = "";
        public int Type { get; set; }
        public string TypeName { get; set; } = "";
        public string Name { get; set; } = "";
        public string TechType { get; set; } = "";

        public int? StatusCode { get; set; }
        public int? ProgramId { get; set; }
        public string? Program { get; set; }
        public string? ProgramPhase { get; set; }
        public int? RemainingMinutes { get; set; }
        public int? ElapsedMinutes { get; set; }
        /// <summary>The time until a delayed start begins.</summary>
        public int? StartDelayMinutes { get; set; }

        public double? Temperature { get; set; }
        public double? TargetTemperature { get; set; }
        public double? CoreTemperature { get; set; }
        public double? CoreTargetTemperature { get; set; }
        /// <summary>The appliance lists food probe temperatures, even while none is plugged in.</summary>
        public bool HasCoreProbe { get; set; }

        public bool? Door { get; set; }
        public bool? Failure { get; set; }
        public bool? Info { get; set; }
        public bool? RemoteStart { get; set; }
        public int? SpinningSpeed { get; set; }

        /// <summary>The running program's energy so far (kWh), from ecoFeedback.</summary>
        public double? Energy { get; set; }
        /// <summary>The running program's water so far (l), from ecoFeedback.</summary>
        public double? Water { get; set; }
        /// <summary>The selected program's energy use relative to the most it can use (%).</summary>
        public double? EnergyForecast { get; set; }
        public double? WaterForecast { get; set; }

        public int? TwinDos1Level { get; set; }
        public int? TwinDos2Level { get; set; }

        public bool Connected => StatusCode is not MieleCodes.NotConnected;

        public string? StatusName => StatusCode is int code ? MieleCodes.StatusName(code) : null;

        public MieleAppliance Copy() => (MieleAppliance)MemberwiseClone();

        /// <summary>Reads an appliance from Miele's <c>{ident, state}</c>.</summary>
        public static MieleAppliance Parse(string id, JsonObject json)
        {
            var ident = json["ident"] as JsonObject;
            var state = json["state"] as JsonObject;
            var appliance = new MieleAppliance
            {
                Id = id,
                Type = Int(ident?["type"]?["value_raw"]) ?? 0,
                TypeName = Text(ident?["type"]?["value_localized"]) ?? "",
                TechType = Text(ident?["deviceIdentLabel"]?["techType"]) ?? ""
            };
            var name = Text(ident?["deviceName"]);
            appliance.Name = !string.IsNullOrWhiteSpace(name) ? name.Trim()
                : appliance.TypeName.Length > 0 ? appliance.TypeName : $"Miele {id}";
            if (state == null)
                return appliance;

            appliance.StatusCode = Int(state["status"]?["value_raw"]);
            appliance.ProgramId = Int(state["ProgramID"]?["value_raw"]);
            appliance.Program = Localized(state["ProgramID"]);
            appliance.ProgramPhase = Localized(state["programPhase"]);
            appliance.RemainingMinutes = Minutes(state["remainingTime"]);
            appliance.ElapsedMinutes = Minutes(state["elapsedTime"]);
            appliance.StartDelayMinutes = Minutes(state["startTime"]);

            appliance.Temperature = ZoneTemperature(state["temperature"]);
            appliance.TargetTemperature = ZoneTemperature(state["targetTemperature"]);
            appliance.CoreTemperature = ZoneTemperature(state["coreTemperature"]);
            appliance.CoreTargetTemperature = ZoneTemperature(state["coreTargetTemperature"]);
            appliance.HasCoreProbe = state["coreTemperature"] is JsonArray { Count: > 0 } || state["coreTargetTemperature"] is JsonArray { Count: > 0 };

            appliance.Door = Bool(state["signalDoor"]);
            appliance.Failure = Bool(state["signalFailure"]);
            appliance.Info = Bool(state["signalInfo"]);
            var remote = state["remoteEnable"] as JsonObject;
            if (remote != null)
                appliance.RemoteStart = Bool(remote["mobileStart"]) == true || Bool(remote["fullRemoteControl"]) == true;
            appliance.SpinningSpeed = Int(state["spinningSpeed"]?["value_raw"]) is int speed && speed > 0 ? speed : null;

            if (state["ecoFeedback"] is JsonObject eco)
            {
                appliance.Energy = Consumption(eco["currentEnergyConsumption"], "wh");
                appliance.Water = Consumption(eco["currentWaterConsumption"], "ml");
                appliance.EnergyForecast = Number(eco["energyForecast"]) is double energy ? Math.Round(energy * 100) : null;
                appliance.WaterForecast = Number(eco["waterForecast"]) is double water ? Math.Round(water * 100) : null;
            }
            return appliance;
        }

        /// <summary>Zone 1's temperature in °C; Miele sends 0.01 °C, and -32768 for none.</summary>
        internal static double? ZoneTemperature(JsonNode? temperatures)
        {
            var zone = (temperatures as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            return Int(zone?["value_raw"]) is int raw && raw != MieleCodes.NoTemperature ? Math.Round(raw / 100.0, 2) : null;
        }

        /// <summary>A Miele time, <c>[hours, minutes]</c>, in minutes.</summary>
        internal static int? Minutes(JsonNode? time) =>
            time is JsonArray { Count: >= 2 } array && Int(array[0]) is int hours && Int(array[1]) is int minutes ? hours * 60 + minutes : null;

        // {unit, value}; a value in the smaller unit (Wh, ml) is converted.
        private static double? Consumption(JsonNode? node, string smallUnit)
        {
            if (Number(node?["value"]) is not double value)
                return null;
            var unit = Text(node?["unit"])?.Trim().ToLowerInvariant();
            return Math.Round(unit == smallUnit ? value / 1000 : value, 3);
        }

        private static string? Localized(JsonNode? node)
        {
            var text = Text(node?["value_localized"])?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

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

        internal static int? Int(JsonNode? node) => Number(node) is double number ? (int)Math.Round(number) : null;

        internal static bool? Bool(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<bool>(out var flag))
                return flag;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag))
                return flag;
            return null;
        }

        internal static string? Text(JsonNode? node)
        {
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<string>(out var text))
                return text;
            return Number(node)?.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What Miele allows the appliance to do right now.</summary>
    internal sealed class MieleActions
    {
        public List<int> ProcessActions { get; set; } = [];
        /// <summary>The shortest and longest delayed start, in minutes; null when a delayed start isn't allowed.</summary>
        public int? StartDelayMin { get; set; }
        public int? StartDelayMax { get; set; }
        public bool PowerOn { get; set; }
        public bool PowerOff { get; set; }
        public List<int> ProgramIds { get; set; } = [];
        public List<MieleTemperatureRange> TargetTemperatures { get; set; } = [];

        public static MieleActions Parse(JsonObject? json)
        {
            var actions = new MieleActions();
            if (json == null)
                return actions;
            actions.ProcessActions = Ints(json["processAction"]);
            if (json["startTime"] is JsonArray { Count: >= 2 } start)
            {
                actions.StartDelayMin = MieleAppliance.Minutes(start[0]);
                actions.StartDelayMax = MieleAppliance.Minutes(start[1]);
            }
            actions.PowerOn = MieleAppliance.Bool(json["powerOn"]) == true;
            actions.PowerOff = MieleAppliance.Bool(json["powerOff"]) == true;
            actions.ProgramIds = Ints(json["programId"]);
            actions.TargetTemperatures = (json["targetTemperature"] as JsonArray)?.OfType<JsonObject>()
                .Select(x => new MieleTemperatureRange(MieleAppliance.Int(x["zone"]) ?? 1, MieleAppliance.Number(x["min"]), MieleAppliance.Number(x["max"])))
                .ToList() ?? [];
            return actions;
        }

        private static List<int> Ints(JsonNode? node) =>
            (node as JsonArray)?.Select(MieleAppliance.Int).OfType<int>().ToList() ?? [];
    }

    internal sealed record MieleTemperatureRange(int Zone, double? Min, double? Max);

    /// <summary>A program that can be selected remotely, with its optional temperature (°C) and duration (minutes).</summary>
    internal sealed record MieleProgram(int Id, string Name, double? TemperatureMin, double? TemperatureMax, double? TemperatureStep, bool TemperatureMandatory,
        int? DurationMin, int? DurationMax, bool DurationMandatory)
    {
        public static List<MieleProgram> ParseAll(JsonNode? json) =>
            (json as JsonArray)?.OfType<JsonObject>().Select(Parse).Where(x => x.Id != 0).ToList() ?? [];

        private static MieleProgram Parse(JsonObject json)
        {
            var temperature = json["parameters"]?["temperature"] as JsonObject;
            var duration = json["parameters"]?["duration"] as JsonObject;
            return new MieleProgram(
                MieleAppliance.Int(json["programId"]) ?? 0,
                MieleAppliance.Text(json["program"])?.Trim() ?? "",
                MieleAppliance.Number(temperature?["min"]),
                MieleAppliance.Number(temperature?["max"]),
                MieleAppliance.Number(temperature?["step"]) is double step && step > 0 ? step : null,
                MieleAppliance.Bool(temperature?["mandatory"]) == true,
                MieleAppliance.Minutes(duration?["min"]),
                MieleAppliance.Minutes(duration?["max"]),
                MieleAppliance.Bool(duration?["mandatory"]) == true);
        }
    }

    /// <summary>Miele's codes for appliance types, statuses and process actions.</summary>
    internal static class MieleCodes
    {
        public const int NoTemperature = -32768;

        public const int Off = 1;
        public const int On = 2;
        public const int Programmed = 3;
        public const int WaitingToStart = 4;
        public const int Running = 5;
        public const int Pause = 6;
        public const int ProgramEnded = 7;
        public const int Failure = 8;
        public const int ProgramInterrupted = 9;
        public const int Idle = 10;
        public const int RinseHold = 11;
        public const int NotConnected = 255;

        public const int Start = 1;
        public const int Stop = 2;
        public const int PauseAction = 3;

        private static readonly Dictionary<int, string> _statuses = new()
        {
            [0] = "reserved", [Off] = "off", [On] = "on", [Programmed] = "programmed", [WaitingToStart] = "waitingToStart",
            [Running] = "running", [Pause] = "pause", [ProgramEnded] = "programEnded", [Failure] = "failure",
            [ProgramInterrupted] = "programInterrupted", [Idle] = "idle", [RinseHold] = "rinseHold", [12] = "service",
            [13] = "superfreezing", [14] = "supercooling", [15] = "superheating", [146] = "supercoolingSuperfreezing",
            [147] = "autocleaning", [NotConnected] = "notConnected"
        };

        public static IReadOnlyList<string> StatusNames { get; } = _statuses.Values.ToList();

        public static string StatusName(int code) => _statuses.TryGetValue(code, out var name) ? name : $"status{code}";

        // Washing machines, dryers and washer-dryers, and dishwashers report the energy and water of a run.
        private static readonly HashSet<int> _washers = [1, 3, 5, 24];
        private static readonly HashSet<int> _consumers = [1, 2, 3, 4, 5, 6, 7, 8, 9, 24];
        private static readonly HashSet<int> _ovens = [12, 13, 15, 31, 45, 67, 73];

        public static bool IsWasher(int type) => _washers.Contains(type);

        public static bool HasConsumption(int type) => _consumers.Contains(type);

        public static bool IsOven(int type) => _ovens.Contains(type);
    }

    /// <summary>A run that ended: what was run, when, and what it used.</summary>
    internal sealed record MieleRun(string? Program, DateTimeOffset StartedAt, DateTimeOffset EndedAt, double? Energy, double? Water)
    {
        public int DurationMinutes => (int)Math.Round((EndedAt - StartedAt).TotalMinutes);
    }

    /// <summary>A run in progress: kept in the appliance's state file, so a restart in the middle of a run still reports it.</summary>
    internal sealed class MieleOpenRun
    {
        public string? Program { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public double? Energy { get; set; }
        public double? Water { get; set; }
    }

    /// <summary>The day's totals and the open run of one appliance.</summary>
    internal sealed class MieleRunState
    {
        public string Day { get; set; } = "";
        public double EnergyToday { get; set; }
        public double WaterToday { get; set; }
        public MieleOpenRun? Run { get; set; }
    }

    internal sealed record MieleRunUpdate(string? Event, MieleRun? Finished, bool Changed);

    /// <summary>
    /// Follows an appliance's runs from its status: a run starts when the appliance starts running (not when it
    /// resumes from a pause), and ends when the program ends, fails or is interrupted, or the appliance stops in the
    /// middle of it. A run's energy and water are the most Miele reported during it, because Miele clears them after.
    /// </summary>
    internal static class MieleRuns
    {
        public const string Started = "started";
        public const string Finished = "finished";
        public const string Interrupted = "interrupted";
        public const string Failed = "failed";

        public static readonly IReadOnlyList<string> Events = [Started, Finished, Interrupted, Failed];

        private static readonly HashSet<int> _endsRun =
        [
            MieleCodes.ProgramEnded, MieleCodes.Failure, MieleCodes.ProgramInterrupted, MieleCodes.Off, MieleCodes.On,
            MieleCodes.Programmed, MieleCodes.WaitingToStart, MieleCodes.Idle
        ];

        public static MieleRunUpdate Update(MieleRunState state, MieleAppliance appliance, DateTimeOffset now, string day)
        {
            var changed = false;
            if (state.Day != day)
            {
                state.Day = day;
                state.EnergyToday = 0;
                state.WaterToday = 0;
                changed = true;
            }

            var status = appliance.StatusCode;
            var run = state.Run;
            if (run == null)
            {
                if (status != MieleCodes.Running)
                    return new MieleRunUpdate(null, null, changed);
                state.Run = new MieleOpenRun
                {
                    Program = appliance.Program,
                    StartedAt = now.AddMinutes(-(appliance.ElapsedMinutes ?? 0)),
                    Energy = appliance.Energy,
                    Water = appliance.Water
                };
                return new MieleRunUpdate(Started, null, true);
            }

            var energy = Max(run.Energy, appliance.Energy);
            var water = Max(run.Water, appliance.Water);
            if (energy != run.Energy || water != run.Water || (run.Program == null && appliance.Program != null))
            {
                run.Energy = energy;
                run.Water = water;
                run.Program ??= appliance.Program;
                changed = true;
            }
            if (status is not int code || !_endsRun.Contains(code))
                return new MieleRunUpdate(null, null, changed);

            state.Run = null;
            state.EnergyToday = Math.Round(state.EnergyToday + (run.Energy ?? 0), 3);
            state.WaterToday = Math.Round(state.WaterToday + (run.Water ?? 0), 3);
            var name = code switch
            {
                MieleCodes.ProgramEnded => Finished,
                MieleCodes.Failure => Failed,
                _ => Interrupted
            };
            return new MieleRunUpdate(name, new MieleRun(run.Program, run.StartedAt, now, run.Energy, run.Water), true);
        }

        private static double? Max(double? a, double? b) => a is double x && b is double y ? Math.Max(x, y) : a ?? b;
    }

    /// <summary>The OAuth tokens of a Miele app, and the authorization code they came from, so it isn't used twice.</summary>
    internal sealed class MieleTokens
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string? UsedCode { get; set; }
    }
}
