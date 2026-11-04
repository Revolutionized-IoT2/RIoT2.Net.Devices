// Ported from eufy-sdk src/transport/p2p/commands.ts, src/transport/p2p/envelope.ts and
// src/transport/p2p/write-commands.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// Only the command ids the guard-mode write and the live snapshot use are carried over.

using System.Buffers.Binary;
using System.Text;

namespace RIoT2.Net.Devices.Services.Eufy.P2P
{
    /// <summary>The P2P command ids (the <c>cmd</c> word of an "XZYH" data frame) this port drives or reads.</summary>
    internal static class P2PCommandType
    {
        /// <summary>CMD_BIND_BROADCAST; also the inner <c>commandType</c> START_LIVE of an own-session media start.</summary>
        public const int StartLive = 1000;
        public const int StartRealtimeMedia = 1003;
        public const int StopRealtimeMedia = 1004;
        /// <summary>Sent once on connect to prompt the station to report; the reply carries the level-2 key envelope.</summary>
        public const int GatewayInfo = 1100;
        /// <summary>CMD_CAMERA_INFO: a camera reporting its own params as a root-level array.</summary>
        public const int CameraInfo = 1103;
        /// <summary>CMD_PING; the keepalive that holds an own-session live stream open.</summary>
        public const int StreamKeepalive = 1139;
        /// <summary>CMD_SET_ARMING (app GUARD_MODE): the guard-mode param and the direct T8410C frame.</summary>
        public const int SetArming = 1224;
        public const int VideoFrame = 1300;
        public const int AudioFrame = 1301;
        public const int SetPayload = 1350;
        public const int NotifyPayload = 1351;
        /// <summary>CMD_DOORBELL_SET_PAYLOAD; the generic control wrapper <c>{commandType,data}</c>.</summary>
        public const int ControlPayload = 1700;
    }

    /// <summary>
    /// The envelope / routing command ids the router itself issues on the wire (the transport-side counterpart
    /// of the capability layer's feature ids).
    /// </summary>
    internal static class P2PEnvelope
    {
        /// <summary>SET_PAYLOAD wrapper: the <c>{account_id,cmd,mChannel,mValue3,payload}</c> envelope.</summary>
        public const int SetPayload = 1350;
        /// <summary>Generic control wrapper: bare <c>{commandType,data}</c>.</summary>
        public const int ControlPayload = 1700;
    }

    /// <summary>Direct-binary write helpers: packed structs rather than JSON recipes.</summary>
    internal static class P2PWriteCommands
    {
        private static void WriteAccountIdField(byte[] body, int offset, string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("control command requires a non-empty account_id", nameof(accountId));
            var text = accountId.Length > 128 ? accountId[..128] : accountId;
            var bytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, body, offset, Math.Min(bytes.Length, body.Length - offset));
        }

        /// <summary>
        /// The level-2 direct-binary control body: <c>[u32 LE channel?][u32 LE value][account_id ASCII pad→128]</c>.
        /// With a channel it is the 136-byte device form; without, the 132-byte station form (a station-scoped
        /// scalar addressed by the outer frame's channel).
        /// </summary>
        public static byte[] BuildDirectBinaryBody(uint value, string accountId, int? channel = null)
        {
            var withChannel = channel.HasValue;
            var body = new byte[(withChannel ? 8 : 4) + 128];
            var offset = 0;
            if (withChannel)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)channel!.Value);
                offset = 4;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), value);
            WriteAccountIdField(body, offset + 4, accountId);
            return body;
        }
    }
}
