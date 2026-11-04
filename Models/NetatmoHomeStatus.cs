namespace RIoT2.Net.Devices.Models
{
    /// <summary>The answer of <c>api/homestatus</c>: the current state of a home's modules and persons.</summary>
    public class NetatmoHomeStatus
    {
        public string status { get; set; }
        public long time_server { get; set; }
        public HomeStatusBody? body { get; set; }
    }

    public class HomeStatusBody
    {
        public HomeStatusHome? home { get; set; }
    }

    public class HomeStatusHome
    {
        public string id { get; set; }
        public List<HomeStatusModule>? modules { get; set; }
        public List<HomeStatusPerson>? persons { get; set; }
    }

    // Each module type has its own fields: every one is optional.
    public class HomeStatusModule
    {
        public string id { get; set; }
        public string? type { get; set; }
        public bool? reachable { get; set; }
        /// <summary>Cameras: <c>on</c>, <c>off</c> or <c>disconnected</c>.</summary>
        public string? monitoring { get; set; }
        /// <summary>Outdoor cameras: <c>on</c>, <c>off</c> or <c>auto</c>.</summary>
        public string? floodlight { get; set; }
        /// <summary>Battery modules: <c>full</c>, <c>high</c>, <c>medium</c>, <c>low</c> or <c>very_low</c>.</summary>
        public string? battery_state { get; set; }
        public int? battery_level { get; set; }
        public string? status { get; set; }
        public int? wifi_strength { get; set; }
        public int? rf_strength { get; set; }
        public int? sd_status { get; set; }
        public int? alim_status { get; set; }
        public bool? is_local { get; set; }
        public string? vpn_url { get; set; }
    }

    public class HomeStatusPerson
    {
        public string id { get; set; }
        public long? last_seen { get; set; }
        public bool? out_of_sight { get; set; }
    }

    /// <summary>The answer of Netatmo's commands (<c>setstate</c>, <c>setpersonshome</c>, ...).</summary>
    public class NetatmoCommandResult
    {
        public string? status { get; set; }
        public long time_server { get; set; }
    }

    /// <summary>The answer of <c>GET webhooks/v1</c>: the URLs registered for the app.</summary>
    public class NetatmoWebhookList
    {
        public string? status { get; set; }
        public List<NetatmoWebhookUrl>? body { get; set; }
    }

    public class NetatmoWebhookUrl
    {
        public string? url { get; set; }
    }
}
