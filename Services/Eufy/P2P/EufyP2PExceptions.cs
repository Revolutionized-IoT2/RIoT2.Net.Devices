// Ported from eufy-sdk src/core/contracts.ts (StationUnreachableError, StationKeyUnavailableError,
// DeviceChannelUnresolvedError) @ 5f92bea5 (Apache-2.0), modified for RIoT2.

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>A P2P station operation failed.</summary>
    public class EufyP2PException : Exception
    {
        public EufyP2PException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>No path to the station was established within the connect deadline, or the session closed before it was.</summary>
    public sealed class EufyStationUnreachableException : EufyP2PException
    {
        public EufyStationUnreachableException(string stationSerial, string message, Exception? inner = null) : base(message, inner)
        {
            StationSerial = stationSerial;
        }

        public string StationSerial { get; }
    }

    /// <summary>The station never negotiated the level-2 key a command needs.</summary>
    public sealed class EufyStationKeyUnavailableException : EufyP2PException
    {
        public EufyStationKeyUnavailableException(string stationSerial)
            : base($"station {stationSerial} offered no level-2 key: the command cannot be sealed for it")
        {
            StationSerial = stationSerial;
        }

        public string StationSerial { get; }
    }

    /// <summary>The station answered a command with a non-zero result code.</summary>
    public sealed class EufyP2PCommandException : EufyP2PException
    {
        public EufyP2PCommandException(string message, int? code = null) : base(message)
        {
            Code = code;
        }

        /// <summary>The station's result code, when it reported one.</summary>
        public int? Code { get; }
    }
}
