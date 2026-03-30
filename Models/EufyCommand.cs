using RIoT2.Core.Utils;

namespace RIoT2.Net.Devices.Models
{
    internal class EufyCommand
    {
        public string MessageId { get; set; }
        public string Command { get; set; }
        public string SerialNumber { get; set; } = null;
        public int? SchemaVersion { get; set; } = null;
        public string VerifyCode { get; set; } = null;
        public string CaptchaId { get; set; } = null;
        public string Captcha { get; set; } = null;
        public string File { get; set; } = null;
        public string Name { get; set; } = null;
        public object Value { get; set; } = null;
        public string[] SerialNumbers { get; set; } = null;
        public string StartDate { get; set; } = null;
        public string EndDate { get; set; } = null;

        private string getAsJson()
        {
            return Json.SerializeAutoTypeNameHandling(this);
        }

        #region Commands
        public static string GetCommand_SetApiSchema(string messageId, int version)
        {
            var c = new EufyCommand()
            {
                MessageId = "set_api_schema_" + messageId,
                Command = "set_api_schema",
                SchemaVersion = version
            };
            return c.getAsJson();
        }

        public static string GetCommand_StartListening(string messageId)
        {
            var c = new EufyCommand()
            {
                MessageId = "start_listening_" + messageId,
                Command = "start_listening"
            };
            return c.getAsJson();
        }

        public static string GetCommand_StationGetProperties(string messageId, string stationSerialNumber)
        {
            var c = new EufyCommand()
            {
                MessageId = "station.get_properties_" + messageId,
                Command = "station.get_properties",
                SerialNumber = stationSerialNumber
            };
            return c.getAsJson();
        }

        public static string GetCommand_DeviceGetProperties(string messageId, string deviceSerialNumber)
        {
            var c = new EufyCommand()
            {
                MessageId = "device.get_properties_" + messageId,
                Command = "device.get_properties",
                SerialNumber = deviceSerialNumber
            };
            return c.getAsJson();
        }

        public static string GetCommand_StationSetProperties(string messageId, string stationSerialNumber, string propName, object propValue)
        {
            var c = new EufyCommand()
            {
                MessageId = messageId,
                Command = "station.set_property",
                SerialNumber = stationSerialNumber,
                Name = propName,
                Value = propValue
            };
            return c.getAsJson();
        }


        public static string GetCommand_StationDatabaseQueryLatestInfo(string messageId, string stationSerialNumber)
        {
            var c = new EufyCommand()
            {
                MessageId = messageId,
                Command = "station.database_query_latest_info",
                SerialNumber = stationSerialNumber,

            };
            return c.getAsJson();
        }

        public static string GetCommand_StationDownloadImage(string messageId, string stationSerialNumber, string file)
        {
            var c = new EufyCommand()
            {
                MessageId = messageId,
                Command = "station.download_image",
                SerialNumber = stationSerialNumber,
                File = file

            };
            return c.getAsJson();
        }

        public static string GetCommand_StationDatabaseQueryByDate(string messageId, string stationSerialNumber, string[] deviceSerials, string startDate, string endDate)
        {
            var c = new EufyCommand()
            {
                MessageId = messageId,
                Command = "station.database_query_by_date",
                SerialNumber = stationSerialNumber,
                SerialNumbers = deviceSerials,
                StartDate = startDate, //YYYYMMDD
                EndDate = endDate //YYYYMMDD

            };
            return c.getAsJson();
        }

        #endregion
    }
}
