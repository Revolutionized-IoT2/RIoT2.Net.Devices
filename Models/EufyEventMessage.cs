namespace RIoT2.Net.Devices.Models
{
    public class EufyEventMessage
    {
        public string Type { get; set; }
        public EufyEvent Event { get; set; }

    }

    public class EufyEvent
    {
        public string Source { get; set; }
        public string Event { get; set; }
        public string SerialNumber { get; set; }
        public string Name { get; set; }
        public object Value { get; set; }
    }

    public class EufyEventData
    {
        public string Device_Sn { get; set; }
        public int Event_Count { get; set; }
        public string Crop_Cloud_Path { get; set; }
        public string Crop_Local_Path { get; set; }
        public string Device_Type { get; set; }
        public string Start_Time { get; set; }
        public string Storage_Path { get; set; }
        public string End_time { get; set; }
        public string Thumb_path { get; set; }
        public int Trigger_type { get; set; }
        public int Video_type { get; set; }
        public int Record_id { get; set; }
    }

    public class EufyImage 
    {
        public EufyData Data { get; set; }
        public ImageType Type { get; set; }
    }

    public class EufyData 
    {
        public string Type { get; set; }
        public byte[] Data { get; set; }
    }

    public class ImageType
    {
        public string Ext { get; set; }
        public string Mime { get; set; }
    }

    public class EufyDownloadCommand
    {
        public EufyDownloadCommandData Command { get; set; }
    }

    public class EufyDownloadCommandData
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
}