// Ported from eufy-sdk src/model/push-events.ts @ 5f92bea5 (Apache-2.0), modified for RIoT2.
// Member names keep upstream's spelling so DetectionName returns upstream's labels.

#pragma warning disable CA1707 // upstream's UPPER_SNAKE names are the labels DetectionName returns

namespace RIoT2.Net.Devices.Services.Eufy.Push
{
    /// <summary>Generic custom push event (field <c>a</c> in CusPushData).</summary>
    internal enum CusPushEvent
    {
        SECURITY = 1,
        TFCARD = 2,
        DOOR_SENSOR = 3,
        CAM_STATE = 4,
        GSENSOR = 5,
        BATTERY_LOW = 6,
        BATTERY_HOT = 7,
        LIGHT_STATE = 8,
        MODE_SWITCH = 9,
        ALARM = 10,
        BATTERY_FULL = 11,
        REPEATER_RSSI_WEAK = 12,
        UPGRADE_STATUS = 13,
        MOTION_SENSOR_PIR = 14,
        ALARM_DELAY = 16,
        HUB_BATT_POWERED = 17,
        SENSOR_NO_OPEN = 18,
        SMART_DROP = 20,
    }

    /// <summary>Alarm trigger source.</summary>
    internal enum CusPushAlarmType
    {
        HUB_STOP = 0,
        DEV_STOP = 1,
        GSENSOR = 2,
        PIR = 3,
        APP = 4,
        HOT = 5,
        DOOR = 6,
        CAMERA = 7,
        MOTION_SENSOR = 8,
        CAMERA_GSENSOR = 9,
        CAMERA_APP = 10,
        CAMERA_LINKAGE = 11,
        HUB_LINKAGE = 12,
        HUB_KEYPAD_PANIC_BUTTON = 13,
        HUB_KEYPAD_EMERGENCY_CODE = 14,
        HUB_STOP_BY_KEYPAD = 15,
        HUB_STOP_BY_APP = 16,
        HUB_STOP_BY_HUB = 17,
        HUB_KEYPAD_CUSTOM_NOT_MAP = 18,
    }

    /// <summary>Doorbell AI-detection events (3xxx).</summary>
    internal enum DoorbellPushEvent
    {
        BACKGROUND_ACTIVE = 3100,
        MOTION_DETECTION = 3101,
        FACE_DETECTION = 3102,
        PRESS_DOORBELL = 3103,
        PET_DETECTION = 3106,
        VEHICLE_DETECTION = 3107,
        PACKAGE_DELIVERED = 3301,
        PACKAGE_TAKEN = 3302,
        FAMILY_DETECTION = 3303,
        PACKAGE_STRANDED = 3304,
        SOMEONE_LOITERING = 3305,
        RADAR_MOTION_DETECTION = 3306,
        AWAY_FROM_HOME = 3307,
        RADAR_DETECTION = 3308,
    }

    /// <summary>Indoor-camera AI-detection events.</summary>
    internal enum IndoorPushEvent
    {
        MOTION_DETECTION = 3101,
        FACE_DETECTION = 3102,
        CRYING_DETECTION = 3104,
        SOUND_DETECTION = 3105,
        PET_DETECTION = 3106,
        VEHICLE_DETECTION = 3107,
    }

    /// <summary>HomeBase-3 paired-device AI-detection events.</summary>
    internal enum HB3PairedDevicePushEvent
    {
        MOTION_DETECTION = 3101,
        FACE_DETECTION = 3102,
        PRESS_DOORBELL = 3103,
        CRYING_DETECTION = 3104,
        SOUND_DETECTION = 3105,
        PET_DETECTION = 3106,
        VEHICLE_DETECTION = 3107,
        DOG_DETECTION = 3108,
        DOG_LICK_DETECTION = 3109,
        DOG_POOP_DETECTION = 3110,
        IDENTITY_PERSON_DETECTION = 3111,
        STRANGER_PERSON_DETECTION = 3112,
    }

    /// <summary>SmartDrop locker events (declared upstream, mapped by no capability).</summary>
    internal enum SmartDropPushEvent
    {
        LOW_BATTERY = 6,
        OVERHEATING_WARNING = 7,
        TAMPERED_WARNING = 10,
        BATTERY_FULLY_CHARGED = 11,
        PERSON_DETECTED = 3102,
    }

    internal static class PushEvents
    {
        /// <summary>Resolve a 3xxx AI-detection event id to a human name (camera/doorbell).</summary>
        public static string DetectionName(int eventType)
        {
            if (Enum.IsDefined(typeof(HB3PairedDevicePushEvent), eventType)) return ((HB3PairedDevicePushEvent)eventType).ToString();
            if (Enum.IsDefined(typeof(DoorbellPushEvent), eventType)) return ((DoorbellPushEvent)eventType).ToString();
            if (Enum.IsDefined(typeof(IndoorPushEvent), eventType)) return ((IndoorPushEvent)eventType).ToString();
            return $"EVENT_{eventType}";
        }
    }
}
