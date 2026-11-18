namespace RIoT2.Net.Devices.Services.EspHome
{
    /// <summary>
    /// The subset of the ESPHome native API (<c>api.proto</c>) a voice satellite needs: message ids, enums and the
    /// messages the client writes. Field numbers are api.proto's.
    /// </summary>
    public static class EspHomeApi
    {
        /// <summary>The API version the client speaks: 1.10 has everything used here.</summary>
        public const int VersionMajor = 1;
        public const int VersionMinor = 10;

        public const int HelloRequest = 1;
        public const int HelloResponse = 2;
        public const int ConnectRequest = 3;
        public const int ConnectResponse = 4;
        public const int DisconnectRequest = 5;
        public const int DisconnectResponse = 6;
        public const int PingRequest = 7;
        public const int PingResponse = 8;
        public const int DeviceInfoRequest = 9;
        public const int DeviceInfoResponse = 10;
        public const int ListEntitiesRequest = 11;
        public const int ListEntitiesBinarySensor = 12;
        public const int ListEntitiesLight = 15;
        public const int ListEntitiesSwitch = 17;
        public const int ListEntitiesDone = 19;
        public const int SubscribeStates = 20;
        public const int BinarySensorState = 21;
        public const int LightState = 24;
        public const int SwitchState = 26;
        public const int LightCommand = 32;
        public const int SwitchCommand = 33;
        public const int GetTimeRequest = 36;
        public const int GetTimeResponse = 37;
        public const int ListEntitiesButton = 61;
        public const int ButtonCommand = 62;
        public const int ListEntitiesMediaPlayer = 63;
        public const int MediaPlayerState = 64;
        public const int MediaPlayerCommand = 65;
        public const int SubscribeVoiceAssistant = 89;
        public const int VoiceAssistantRequest = 90;
        public const int VoiceAssistantResponse = 91;
        public const int VoiceAssistantEvent = 92;
        public const int VoiceAssistantAudio = 106;
        public const int ListEntitiesEvent = 107;
        public const int Event = 108;
        public const int VoiceAssistantTimerEvent = 115;
        public const int VoiceAssistantAnnounce = 119;
        public const int VoiceAssistantAnnounceFinished = 120;
        public const int VoiceAssistantConfigurationRequest = 121;
        public const int VoiceAssistantConfigurationResponse = 122;
        public const int VoiceAssistantSetConfiguration = 123;
        public const int NoiseEncryptionSetKeyRequest = 124;
        public const int NoiseEncryptionSetKeyResponse = 125;

        /// <summary>SubscribeVoiceAssistantRequest.flags: the microphone's audio over this connection.</summary>
        public const uint SubscribeApiAudio = 1;

        /// <summary>VoiceAssistantRequest.flags: the satellite wants the wake word heard on the server.</summary>
        public const uint RequestUseWakeWord = 2;

        public const uint MediaPlayerIdle = 1;
        public const uint MediaPlayerPlaying = 2;
        public const uint MediaPlayerAnnouncing = 4;

        /// <summary>VoiceAssistantEvent.</summary>
        public enum VoiceEvent
        {
            Error = 0,
            RunStart = 1,
            RunEnd = 2,
            SttStart = 3,
            SttEnd = 4,
            IntentStart = 5,
            IntentEnd = 6,
            TtsStart = 7,
            TtsEnd = 8,
            SttVadStart = 11,
            SttVadEnd = 12
        }

        /// <summary>VoiceAssistantTimerEvent.</summary>
        public enum TimerEvent { Started = 0, Updated = 1, Cancelled = 2, Finished = 3 }

        public static ProtoWriter Hello(string clientInfo) =>
            new ProtoWriter().String(1, clientInfo).UInt(2, VersionMajor).UInt(3, VersionMinor);

        /// <summary>VoiceAssistantEventResponse: the event and its data, name and value pairs.</summary>
        public static ProtoWriter VoiceAssistantEventMessage(VoiceEvent type, params (string Name, string Value)[] data)
        {
            var message = new ProtoWriter().UInt(1, (ulong)type);
            foreach (var (name, value) in data)
                message.Message(2, new ProtoWriter().String(1, name).String(2, value));
            return message;
        }

        /// <summary>VoiceAssistantTimerEventResponse.</summary>
        public static ProtoWriter TimerEventMessage(TimerEvent type, string id, string name, uint totalSeconds, uint secondsLeft, bool active) =>
            new ProtoWriter().UInt(1, (ulong)type).String(2, id).String(3, name).UInt(4, totalSeconds).UInt(5, secondsLeft).Bool(6, active);
    }
}
