namespace RIoT2.Net.Devices.Models
{
    /// <summary>One Govee sensor on the account, as the device list describes it.</summary>
    internal sealed class GoveeSensor
    {
        /// <summary>Govee's device id, e.g. <c>FD:C8:DC:1E:D5:51:65:24</c>.</summary>
        public string Device { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool HasTemperature { get; set; }
        public bool HasHumidity { get; set; }
        /// <summary>The property instance that carries the battery level, when the device list has one.</summary>
        public string? BatteryInstance { get; set; }
        /// <summary>The property instances the device has, for the debug log.</summary>
        public List<string> Properties { get; set; } = [];

        /// <summary>The state the last poll read, null before the first one.</summary>
        public GoveeState? State { get; set; }
    }

    /// <summary>A sensor's state. When <see cref="Online"/> is false, Govee's values are history and are left out.</summary>
    internal sealed class GoveeState
    {
        public bool? Online { get; set; }
        /// <summary>°C.</summary>
        public double? Temperature { get; set; }
        /// <summary>%.</summary>
        public double? Humidity { get; set; }
        /// <summary>%.</summary>
        public double? Battery { get; set; }
    }

    /// <summary>One polled temperature (°C).</summary>
    internal sealed class GoveeSample
    {
        public DateTimeOffset Time { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// A sensor's file in <c>Data/</c>: the last hour <c>temperatureHourly</c> reported and the samples of the hours
    /// not reported yet, so a restart neither repeats an hour nor loses its samples.
    /// </summary>
    internal sealed class GoveeSensorFile
    {
        public DateTimeOffset? LastReportedHour { get; set; }
        public List<GoveeSample> Samples { get; set; } = [];
    }

    /// <summary>A finished hour's temperatures.</summary>
    internal sealed record GoveeHour(DateTimeOffset Start, double Average, double Minimum, double Maximum, int Samples);
}
