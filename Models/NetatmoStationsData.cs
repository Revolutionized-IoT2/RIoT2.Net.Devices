namespace RIoT2.Net.Devices.Models
{
    internal class NetatmoStationsData
    {
        public Body body { get; set; }
        public string status { get; set; }
        public double time_exec { get; set; }
        public int time_server { get; set; }
    }

    internal class Body
    {
        public List<Device> devices { get; set; }
        public User user { get; set; }
    }

    internal class User
    {
        public string mail { get; set; }
        public Administrative administrative { get; set; }
    }

    internal class Administrative
    {
        public string country { get; set; }
        public string reg_locale { get; set; }
        public string lang { get; set; }
        public int unit { get; set; }
        public int windunit { get; set; }
        public int pressureunit { get; set; }
        public int feel_like_algo { get; set; }
    }

    internal class Device
    {
        public string _id { get; set; }
        public string cipher_id { get; set; }
        public int last_status_store { get; set; }
        public List<Module> modules { get; set; }
        public Place place { get; set; }
        public string station_name { get; set; }
        public string type { get; set; }
        public DashboardData2 dashboard_data { get; set; }
        public List<string> data_type { get; set; }
        public bool co2_calibrating { get; set; }
        public int date_setup { get; set; }
        public int last_setup { get; set; }
        public string module_name { get; set; }
        public int firmware { get; set; }
        public int last_upgrade { get; set; }
        public int wifi_status { get; set; }
    }

    internal class Place
    {
        public int altitude { get; set; }
        public string city { get; set; }
        public string country { get; set; }
        public string timezone { get; set; }
        public List<double> location { get; set; }
    }

    internal class Module
    {
        public string _id { get; set; }
        public string type { get; set; }
        public int last_message { get; set; }
        public int last_seen { get; set; }
        public DashboardData dashboard_data { get; set; }
        public List<string> data_type { get; set; }
        public string module_name { get; set; }
        public int last_setup { get; set; }
        public int battery_vp { get; set; }
        public int battery_percent { get; set; }
        public int rf_status { get; set; }
        public int firmware { get; set; }
    }

    internal class DashboardData
    {
        public int time_utc { get; set; }
        public double Temperature { get; set; }
        public string temp_trend { get; set; }
        public int Humidity { get; set; }
        public int date_max_temp { get; set; }
        public int date_min_temp { get; set; }
        public double min_temp { get; set; }
        public double max_temp { get; set; }
    }

    internal class DashboardData2
    {
        public double AbsolutePressure { get; set; }
        public int time_utc { get; set; }
        public int Noise { get; set; }
        public double Temperature { get; set; }
        public string temp_trend { get; set; }
        public int Humidity { get; set; }
        public double Pressure { get; set; }
        public string pressure_trend { get; set; }
        public int CO2 { get; set; }
        public int date_max_temp { get; set; }
        public int date_min_temp { get; set; }
        public double min_temp { get; set; }
        public double max_temp { get; set; }
    }
}
