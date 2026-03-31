using System.Text.Json.Serialization;

namespace RIoT2.Net.Devices.Models
{
    public class SecurityReport
    {
        [JsonConstructor]
        public SecurityReport() { }

        public SecurityReport(Subevent netatmoSubEvent)
        {
            ImageUrl = netatmoSubEvent.snapshot.url;
            Message = netatmoSubEvent.message;
            SecurityEvent = getSecurityEventType(netatmoSubEvent.type);
            EventValue = "";
            Source = "netatmo";
        }

        public SecurityReport(Event netatmoEvent, List<Person> knownPersons) 
        {
            ImageUrl = netatmoEvent.snapshot.url;
            Message = netatmoEvent.message;
            SecurityEvent = getSecurityEventType(netatmoEvent.type, netatmoEvent.person_id);
            EventValue = getPersonName(netatmoEvent.person_id, knownPersons);
            Source = "netatmo";
        }

        public string Source { get; set; }
        public string ImageUrl { get; set; }
        public string Message { get; set; }
        public SecurityEventType SecurityEvent { get; set; }
        public string EventValue { get; set; }

        private string getPersonName(string personId, List<Person> knownPersons)
        {
            if (String.IsNullOrEmpty(personId) || knownPersons == null)
                return null;

            var homePerson = knownPersons.FirstOrDefault(x => x.id == personId);
            if (homePerson == null)
                return null;

            return homePerson.pseudo;
        }

        private SecurityEventType getSecurityEventType(string eventType, string personId = null)
        {
            switch (eventType.ToLower()) 
            {
                case "alarm_started":
                    return SecurityEventType.soundDetected;
                case "person":
                case "human":
                case "person_away":
                    if (String.IsNullOrEmpty(personId))
                        return SecurityEventType.personDetected;
                    else
                        return SecurityEventType.personName;
                case "animal":
                    return SecurityEventType.animalDetected;
                case "movement":
                default:
                    return SecurityEventType.motionDetected;
            }
        }
    }

    public enum SecurityEventType 
    {
        personDetected = 0,
        personName = 1,
        motionDetected = 2,
        animalDetected = 3,
        soundDetected = 4,
        strangerPersonDetected = 5,
        vehicleDetected = 6
    }
}
