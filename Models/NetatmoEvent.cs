
namespace RIoT2.Net.Devices.Models
{
    public class NetatmoSecurityEvents
    {
        private List<Event> _netatmoEvents; //30 latest events

        public NetatmoSecurityEvents()
        {
            _netatmoEvents = new List<Event>();
        }

        //This will set current events and return NEW events compared to previous update
        //If there are no previous events latest event from each module is returned
        public List<Event> Update(List<Event> events)
        {
            var newEvents = new List<Event>();
            if (events == null || events.Count == 0)
                return newEvents;

            if (_netatmoEvents.Count == 0) //initial update
            {
                foreach (var moduleId in getModuleIds(events))
                    newEvents.Add(GetLatestByModuleId(events, moduleId));

                _netatmoEvents = events;

                return newEvents;
            }
            else //normal update
            {
                newEvents.AddRange(events.Where(i => !_netatmoEvents.Select(e => e.id).Contains(i.id)));
                _netatmoEvents = events;

                return newEvents;
            }
        }

        public Event GetLatestByModuleId(List<Event> events, string moduleId) 
        {
            var latestEvent = events.OrderByDescending(x => x.time).Where(e => e.snapshot != null || (e.subevents != null && e.subevents.Count > 0 && e.subevents.Any(s => s.snapshot != null))) 
                .FirstOrDefault(m => m.module_id == moduleId);

            //only keep latest subevent that has snapshot
            if (latestEvent.subevents != null) 
            {
                latestEvent.subevents = new List<Subevent>() 
                {
                    latestEvent.subevents.FirstOrDefault(x => x.snapshot != null)
                };
            }
            return latestEvent;
        }

        private List<string> getModuleIds(List<Event> events) 
        {
            return events.Select(x => x.module_id).Distinct().ToList();
        }
    }

    public class NetatmoEvent
    {
        public EventBody body { get; set; }
        public string status { get; set; }
        public float time_exec { get; set; }
        public int time_server { get; set; }
    }

    public class EventBody
    {
        public EventHome home { get; set; }
    }

    public class EventHome
    {
        public string id { get; set; }
        public List<Event> events { get; set; }
    }

    public class Event
    {
        public string id { get; set; }
        public string type { get; set; }
        public int time { get; set; }
        public string module_id { get; set; }
        public string video_id { get; set; }
        public string video_status { get; set; }
        public List<Subevent> subevents { get; set; }
        public Snapshot snapshot { get; set; }
        public Vignette vignette { get; set; }
        public string message { get; set; }
        public string sub_message { get; set; }
        public string person_id { get; set; }
        public bool out_of_sight { get; set; }
        public bool? is_arrival { get; set; }
    }

    public class Snapshot
    {
        public string url { get; set; }
    }

    public class Vignette
    {
        public string url { get; set; }
    }

    public class Subevent
    {
        public string id { get; set; }
        public string type { get; set; }
        public int time { get; set; }
        public bool verified { get; set; }
        public int offset { get; set; }
        public SnapshotSubEvent snapshot { get; set; }
        public VignetteSubEvent vignette { get; set; }
        public string message { get; set; }
    }

    public class SnapshotSubEvent
    {
        public string url { get; set; }
        public string filename { get; set; }
    }

    public class VignetteSubEvent
    {
        public string url { get; set; }
        public string filename { get; set; }
    }
}