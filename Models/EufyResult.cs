namespace RIoT2.Net.Devices.Models
{
    public class EufyResult
    {
        public string Type { get; set; }
        public bool Success { get; set; }
        public string MessageId { get; set; }
        public EufyResultData Result { get; set; }
    }

    public class EufyResultData
    {
        public EufyState State { get; set; }
        public string SerialNumber { get; set; }
        public EufyPropertiesData Properties { get; set; }
    }

    public class EufyState
    {
        public EufyDriver Driver { get; set; }
        public string[] Stations { get; set; }
        public string[] Devices { get; set; }
    }

    public class EufyDriver
    {
        public string Version { get; set; }
        public bool Connected { get; set; }
        public bool PushConnected { get; set; }
        public bool MqttConnected { get; set; }
    }

    public class EufyPropertiesData
    {
        public string Name { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public int Type { get; set; }
        public string MacAddress { get; set; }
        public int GuardMode { get; set; }
        public int CurrentMode { get; set; }
        public bool DogDetected { get; set; }
        public bool DogLickDetected { get; set; }
        public bool DogPoopDetected { get; set; }
        public bool MotionDetected { get; set; }
        public bool PersonDetected { get; set; }
        public string PersonName { get; set; }
        public bool PetDetected { get; set; }
        public bool SoundDetected { get; set; }
        public bool StrangerPersonDetected { get; set; }
        public bool VehicleDetected { get; set; }
        public int BatteryTemperature { get; set; }
        public bool Enabled { get; set; }
        public EufyImage Picture { get; set; }
    }
}
