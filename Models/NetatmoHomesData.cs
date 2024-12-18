namespace RIoT2.Net.Devices.Models
{
    public class NetatmoHomesData
    {
        public HomesDataBody body { get; set; }
        public string status { get; set; }
        public double time_exec { get; set; }
        public int time_server { get; set; }
    }

    public class HomesDataBody
    {
        public List<Home> homes { get; set; }
        public HomesUser user { get; set; }
    }


    public class HomesUser
    {
        public string email { get; set; }
        public string language { get; set; }
        public string locale { get; set; }
        public int feel_like_algorithm { get; set; }
        public int unit_pressure { get; set; }
        public int unit_system { get; set; }
        public int unit_wind { get; set; }
        public string id { get; set; }
    }

    public class Home
    {
        public string id { get; set; }
        public string name { get; set; }
        public int altitude { get; set; }
        public List<double> coordinates { get; set; }
        public string country { get; set; }
        public string timezone { get; set; }
        public List<HomesModule> modules { get; set; }
        public List<Person> persons { get; set; }
    }

    public class HomesModule
    {
        public string id { get; set; }
        public string type { get; set; }
        public string name { get; set; }
        public int setup_date { get; set; }
        public List<string> modules_bridged { get; set; }
        public string bridge { get; set; }
    }

    public class Person
    {
        public string id { get; set; }
        public string pseudo { get; set; }
        public string url { get; set; }
    }
}
