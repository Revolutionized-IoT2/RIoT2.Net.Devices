namespace RIoT2.Net.Devices.Models
{
    internal class NetatmoAuth
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public List<string> scope { get; set; }
        public int expires_in { get; set; }
        public int expire_in { get; set; }
    }
}
