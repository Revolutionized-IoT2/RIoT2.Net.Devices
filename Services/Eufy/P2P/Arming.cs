// Ported from eufy-sdk src/model/capabilities/arming.ts and src/model/capabilities/access.ts (setPayload,
// setStationScalar, describeDevice) @ 5f92bea5 (Apache-2.0), modified for RIoT2.

using System.Text.Json.Nodes;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>A write intent the router turns into frames (the P2P kinds of upstream's <c>Command</c> union this port needs).</summary>
    internal abstract record EufyP2PCommand(int Cmd, int Channel);

    /// <summary>
    /// A <c>SET_PAYLOAD</c> (1350) envelope <c>{account_id,cmd,mChannel,mValue3,payload}</c>. <paramref name="Form"/>
    /// <c>"auto"</c> lets the session's negotiated level pick the seal; null pins level 2.
    /// </summary>
    internal sealed record SetPayloadCommand(int Cmd, JsonObject Payload, int Channel, int? MValue3, string? Form) : EufyP2PCommand(Cmd, Channel);

    /// <summary>A station-scoped scalar: outer command <c>Cmd</c> itself at level 2, body <c>[u32 value][account_id pad→128]</c>.</summary>
    internal sealed record StationScalarCommand(int Cmd, uint Value, int Channel) : EufyP2PCommand(Cmd, Channel);

    /// <summary>What a command builder knows about the target device.</summary>
    internal sealed record ArmingContext(string? Model, int Channel, string? AccountName, int? DeviceType = null, string? Serial = null)
    {
        /// <summary>A one-line device descriptor for error messages.</summary>
        public string Describe() => $"deviceType={DeviceType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} model={Model ?? "?"} serial={Serial ?? "?"} channel={Channel} codec=station";
    }

    /// <summary>The guard (arming) mode write: cmd 1224, as a <c>1350</c> envelope or the T8410C direct frame.</summary>
    internal static class Arming
    {
        /// <summary>CMD_SET_ARMING (app GUARD_MODE): the same id as the read param.</summary>
        public const int SetArmingCmd = 1224;

        /// <summary>
        /// Guard-mode name → the wire's <c>mode_type</c>. All nine are confirmed writes on cmd 1224 (away, home and
        /// disarmed byte-captured; the other six live-confirmed).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> ModeWire = new Dictionary<string, int>
        {
            ["away"] = 0,
            ["home"] = 1,
            ["schedule"] = 2,
            ["custom1"] = 3,
            ["custom2"] = 4,
            ["custom3"] = 5,
            ["off"] = 6,
            ["geo"] = 47,
            ["disarmed"] = 63,
        };

        /// <summary>
        /// The models whose guard-mode write is the direct frame (outer cmd 1224 at level 2, body
        /// <c>u32 LE mode_type ‖ admin account id pad→128</c>) rather than the <c>1350</c> envelope, which the T8410C
        /// answers with result -103.
        /// </summary>
        public static readonly IReadOnlySet<string> DirectArmingModels = new HashSet<string>(StringComparer.Ordinal) { "T8410C" };

        /// <summary>The modes confirmed on the direct frame; any other is refused.</summary>
        public static readonly IReadOnlySet<string> DirectArmingModes = new HashSet<string>(StringComparer.Ordinal) { "away", "home", "disarmed" };

        public static bool IsValidWireMode(int wireMode) => ModeWire.Values.Contains(wireMode);

        /// <summary>The mode name of a wire value, or null.</summary>
        public static string? ModeName(int wireMode) => ModeWire.FirstOrDefault(kv => kv.Value == wireMode).Key;

        /// <summary>
        /// Builds the guard-mode write intent. Throws <see cref="ArgumentOutOfRangeException"/> for a wire value outside
        /// the nine modes, <see cref="InvalidOperationException"/> without an account identity and
        /// <see cref="NotSupportedException"/> for a mode the direct frame is not confirmed for.
        /// </summary>
        public static EufyP2PCommand BuildCommand(int wireMode, ArmingContext ctx)
        {
            var mode = ModeName(wireMode)
                ?? throw new ArgumentOutOfRangeException(nameof(wireMode), wireMode, "guard mode must be one of 0/1/2/3/4/5/6/47/63");
            if (string.IsNullOrEmpty(ctx.AccountName))
                throw new InvalidOperationException($"arming: missing account identity (user_name) [{ctx.Describe()}]");
            if (DirectArmingModels.Contains(ctx.Model ?? ""))
            {
                if (!DirectArmingModes.Contains(mode))
                    throw new NotSupportedException($"arming: mode {mode} is unconfirmed on the direct 1224 frame [{ctx.Describe()}]");
                return new StationScalarCommand(SetArmingCmd, (uint)wireMode, ctx.Channel);
            }
            var payload = new JsonObject
            {
                ["mode_type"] = wireMode,
                ["user_name"] = ctx.AccountName,
            };
            return new SetPayloadCommand(SetArmingCmd, payload, ctx.Channel, 0, "auto");
        }
    }
}
