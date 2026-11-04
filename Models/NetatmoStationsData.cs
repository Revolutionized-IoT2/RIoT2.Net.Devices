namespace RIoT2.Net.Devices.Models
{
    /// <summary>
    /// The answer of <c>api/getstationsdata</c> (weather stations) and <c>api/gethomecoachsdata</c> (Healthy Home
    /// Coaches), which share their shape; a coach has no modules.
    /// </summary>
    public class NetatmoStationsData
    {
        public StationsBody? body { get; set; }
        public string status { get; set; }
        public double time_exec { get; set; }
        public long time_server { get; set; }
    }

    public class StationsBody
    {
        public List<StationDevice>? devices { get; set; }
        public StationsUser? user { get; set; }
    }

    public class StationsUser
    {
        public string? mail { get; set; }
    }

    /// <summary>A station's main module (<c>NAMain</c>) or a Healthy Home Coach (<c>NHC</c>).</summary>
    public class StationDevice : StationModule
    {
        public string? station_name { get; set; }
        public string? home_id { get; set; }
        public string? home_name { get; set; }
        public int? wifi_status { get; set; }
        public bool? co2_calibrating { get; set; }
        public List<StationModule>? modules { get; set; }
    }

    /// <summary>
    /// A module of a station: outdoor (<c>NAModule1</c>), wind gauge (<c>NAModule2</c>), rain gauge (<c>NAModule3</c>) or
    /// indoor (<c>NAModule4</c>).
    /// </summary>
    public class StationModule
    {
        public string _id { get; set; }
        public string? type { get; set; }
        public string? module_name { get; set; }
        /// <summary>What the module measures: <c>Temperature</c>, <c>CO2</c>, <c>Humidity</c>, <c>Noise</c>, <c>Pressure</c>, <c>Rain</c>, <c>Wind</c>, <c>health_idx</c>.</summary>
        public List<string>? data_type { get; set; }
        public bool? reachable { get; set; }
        public int? battery_percent { get; set; }
        public int? rf_status { get; set; }
        public long? last_seen { get; set; }
        public long? last_message { get; set; }
        public DashboardData? dashboard_data { get; set; }
    }

    // The member names are Netatmo's, and the report addresses: <module id>|<member>.
    public class DashboardData
    {
        public long? time_utc { get; set; }
        public double? Temperature { get; set; }
        public double? min_temp { get; set; }
        public double? max_temp { get; set; }
        public string? temp_trend { get; set; }
        public int? CO2 { get; set; }
        public int? Humidity { get; set; }
        public int? Noise { get; set; }
        public double? Pressure { get; set; }
        public double? AbsolutePressure { get; set; }
        public string? pressure_trend { get; set; }
        public double? Rain { get; set; }
        public double? sum_rain_1 { get; set; }
        public double? sum_rain_24 { get; set; }
        public int? WindStrength { get; set; }
        public int? WindAngle { get; set; }
        public int? GustStrength { get; set; }
        public int? GustAngle { get; set; }
        public int? max_wind_str { get; set; }
        public int? max_wind_angle { get; set; }
        public int? health_idx { get; set; }
    }
}
