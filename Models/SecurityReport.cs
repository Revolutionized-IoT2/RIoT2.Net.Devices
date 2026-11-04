using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Models
{
    /// <summary>A security event report: shared by the Netatmo, Eufy and FTP camera devices.</summary>
    public class SecurityReport
    {
        [JsonConstructor]
        public SecurityReport() { }

        public string Source { get; set; }
        public string? ImageUrl { get; set; }
        public string Message { get; set; }
        public SecurityEventType SecurityEvent { get; set; }
        public object? EventValue { get; set; }

        /// <summary>The module (camera, sensor) the event is from, when the source names it.</summary>
        public string? ModuleId { get; set; }

        /// <summary>The source's id of the event, when it has one.</summary>
        public string? EventId { get; set; }

        /// <summary>When the event happened, seconds since the epoch, when the source says.</summary>
        public long? Time { get; set; }

        /// <summary>The person the event is about, when the source recognized one.</summary>
        public string? PersonId { get; set; }
    }

    // Numbers are stored with reports: append, never renumber.
    public enum SecurityEventType 
    {
        personDetected = 0,
        personName = 1,
        motionDetected = 2,
        animalDetected = 3,
        soundDetected = 4,
        strangerPersonDetected = 5,
        vehicleDetected = 6,
        doorbellPressed = 7,
        packageDelivered = 8,
        packageTaken = 9,
        packageStranded = 10,
        cryingDetected = 11,
        /// <summary>A still taken on request, not an event.</summary>
        snapshot = 12,
        smokeDetected = 13,
        coDetected = 14,
        doorOpened = 15,
        tagMoved = 16,
        sirenSounding = 17,
        tampered = 18,
        doorbellCallAccepted = 19,
        doorbellCallMissed = 20,
        connectionLost = 21
    }
}
