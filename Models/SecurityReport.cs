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
                    return SecurityEventType.Alarm;
                case "person":
                    if(String.IsNullOrEmpty(e.person_id))
                        return SecurityEventType.UnknownPersonSeen;
                    else
                        return SecurityEventType.KnownPersonSeen;
                case "person_away":
                    return SecurityEventType.KnownPersonAway;
                case "movement":
                default:
                    return SecurityEventType.Movement;
            }
        }

        private SecurityEventType getSecurityEventType(Subevent e)
        {
            switch (e.type.ToLower())
            {
                case "human":
                    return SecurityEventType.UnknownPersonSeen;
                case "animal":
                    return SecurityEventType.AnimalSeen;
                case "vehicle":
                default:
                    return SecurityEventType.Movement;
            }
        }
    }

    public enum SecurityEventType 
    {
        UnknownPersonSeen = 0,
        KnownPersonSeen = 1,
        Movement = 2,
        AnimalSeen = 3,
        Alarm = 4,
        KnownPersonAway = 5,
        KnownPersonHome = 6
    }
}
