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
            SecurityEvent = getSecurityEventType(netatmoSubEvent);
            Subject = netatmoSubEvent.type;
            Source = "netatmo";
        }

        public SecurityReport(Event netatmoEvent, List<Person> knownPersons) 
        {
            ImageUrl = netatmoEvent.snapshot.url;
            Message = netatmoEvent.message;
            SecurityEvent = getSecurityEventType(netatmoEvent);
            Subject = getSubject(netatmoEvent, knownPersons);
            Source = "netatmo";
        }

        public string Source { get; set; }
        public string ImageUrl { get; set; }
        public string Message { get; set; }
        public SecurityEventType SecurityEvent { get; set; }
        public string Subject { get; set; }

        private string getSubject(Event e, List<Person> knownPersons)
        {
            if (String.IsNullOrEmpty(e.person_id) || knownPersons == null)
                return null;

            var homePerson = knownPersons.FirstOrDefault(x => x.id == e.person_id);
            if (homePerson == null)
                return null;

            return homePerson.pseudo;
        }

        private SecurityEventType getSecurityEventType(Event e)
        {
            switch (e.type.ToLower()) 
            {
                case "alarm_started":
                    return SecurityEventType.soundDetected;
                case "person":
                    if(String.IsNullOrEmpty(e.person_id))
                        return SecurityEventType.personDetected;
                    else
                        return SecurityEventType.personName;
                case "person_away":
                    return SecurityEventType.personNameAway;
                case "movement":
                default:
                    return SecurityEventType.motionDetected;
            }
        }

        private SecurityEventType getSecurityEventType(Subevent e)
        {
            switch (e.type.ToLower())
            {
                case "human":
                    return SecurityEventType.personDetected;
                case "animal":
                    return SecurityEventType.petDetected;
                case "vehicle":
                    return SecurityEventType.vehicleDetected;
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
        petDetected = 3,
        soundDetected = 4,
        personNameAway = 5,
        knownPersonHome = 6,
        strangerPersonDetected=7,
        vehicleDetected = 8,
        picture = 9,
    }
}
