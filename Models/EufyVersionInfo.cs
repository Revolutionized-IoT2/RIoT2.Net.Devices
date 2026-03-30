namespace RIoT2.Net.Devices.Models
{
    public class EufyVersionInfo
    {
        public string Type { get; set; }
        public string DriverVersion { get; set; }
        public string ServerVersion { get; set; }
        public int MinSchemaVersion { get; set; }
        public int MaxSchemaVersion { get; set; }
    }
}