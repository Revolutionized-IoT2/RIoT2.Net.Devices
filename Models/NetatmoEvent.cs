namespace RIoT2.Net.Devices.Models
{
    /// <summary>The answer of <c>api/getevents</c>: a home's latest security events, newest first.</summary>
    public class NetatmoEvent
    {
        public EventBody? body { get; set; }
        public string status { get; set; }
        public double time_exec { get; set; }
        public long time_server { get; set; }
    }

    public class EventBody
    {
        public EventHome? home { get; set; }
    }

    public class EventHome
    {
        public string id { get; set; }
        public List<Event>? events { get; set; }
    }

    public class Event : Subevent
    {
        public string module_id { get; set; }
        public List<Subevent>? subevents { get; set; }
        public string? sub_message { get; set; }
        public string? person_id { get; set; }
    }

    public class Subevent
    {
        public string id { get; set; }
        public string type { get; set; }
        public long time { get; set; }
        public NetatmoDocument? snapshot { get; set; }
        public NetatmoDocument? vignette { get; set; }
        public string? message { get; set; }
    }

    public class NetatmoDocument
    {
        public string? url { get; set; }
        public string? filename { get; set; }
    }
}
