namespace RIoT2.Net.Devices.Models
{
    internal class NetatmoHomeStatus
    {
        public string status { get; set; }
        public int time_server { get; set; }
        public HomeStatusBody body { get; set; }
    }

    internal class HomeStatusBody
    {
        public HomeStatusHome home { get; set; }
    }

    internal class HomeStatusHome
    {
        public string id { get; set; }
        public List<HomeStatusModule> modules { get; set; }
        public List<HomeStatusPerson> persons { get; set; }
    }

    internal class HomeStatusModule
    {
        public string id { get; set; }
        public string type { get; set; }
        public int firmware_revision { get; set; }
        public string wifi_state { get; set; }
        public int wifi_strength { get; set; }
        public int sd_status { get; set; }
        public int alim_status { get; set; }
        public string vpn_url { get; set; }
        public bool is_local { get; set; }
        public string monitoring { get; set; }
        public string floodlight { get; set; }
    }

    internal class HomeStatusPerson
    {
        public string id { get; set; }
        public int last_seen { get; set; }
        public bool out_of_sight { get; set; }
    }
}
