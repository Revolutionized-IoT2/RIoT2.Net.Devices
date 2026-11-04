// Ported from eufy-sdk src/transport/push/message-tags.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>The 1-byte tag preceding each framed message on Google's MCS/FCM socket.</summary>
    internal enum McsMessageTag : byte
    {
        HeartbeatPing = 0,
        HeartbeatAck = 1,
        LoginRequest = 2,
        LoginResponse = 3,
        Close = 4,
        MessageStanza = 5,
        PresenceStanza = 6,
        IqStanza = 7,
        DataMessageStanza = 8,
        BatchPresenceStanza = 9,
        StreamErrorStanza = 10,
        HttpRequest = 11,
        HttpResponse = 12,
        BindAccountRequest = 13,
        BindAccountResponse = 14,
        TalkMetadata = 15,
        NumProtoTypes = 16,
    }
}
