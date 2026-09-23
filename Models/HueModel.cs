namespace RIoT2.Net.Devices.Models
{
    // Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(myJsonResponse);
    public class HueAlert
    {
        public List<string> action_values { get; set; }
    }

    public class HueBlue
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    public class HueGreen
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    public class HueRed
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    public class HueGamut
    {
        public HueBlue blue { get; set; }
        public HueGreen green { get; set; }
        public HueRed red { get; set; }
    }

    public class HueXy
    {
        public double x { get; set; }
        public double y { get; set; }
    }

    public class HueColor
    {
        public HueGamut gamut { get; set; }
        public string gamut_type { get; set; }
        public HueXy xy { get; set; }
        public static HueXy ConvertRGBToHueColor(double red, double green, double blue, out double brightness)
        {
            red = (red > 0.04045) ? Math.Pow((red + 0.055) / (1.0 + 0.055), 2.4) : (red / 12.92);
            green = (green > 0.04045) ? Math.Pow((green + 0.055) / (1.0 + 0.055), 2.4) : (green / 12.92);
            blue = (blue > 0.04045) ? Math.Pow((blue + 0.055) / (1.0 + 0.055), 2.4) : (blue / 12.92);

            double X = red * 0.4124 + green * 0.3576 + blue * 0.1805;
            double Y = red * 0.2126 + green * 0.7152 + blue * 0.0722;
            double Z = red * 0.0193 + green * 0.1192 + blue * 0.9505;

            double x = X / (X + Y + Z);
            double y = Y / (X + Y + Z);
            brightness = Y;

            return new HueXy()
            {
                x = x,
                y = y
            };
        }
        public RGB ConvertToRGB(double brightness) 
        {
            double z = 1.0f - xy.x - xy.y;
            double Y = brightness; // The given brightness value
            double X = (Y / xy.y) * xy.x;
            double Z = (Y / xy.y) * z;

            double r = X * 1.656492 - Y * 0.354851 - Z * 0.255038;
            double g = -X * 0.707196 + Y * 1.655397 + Z * 0.036152;
            double b = X * 0.051713 - Y * 0.121364 + Z * 1.011530;

            r = r <= 0.0031308 ? 12.92 * r : (1.0 + 0.055) * Math.Pow(r, (1.0 / 2.4)) - 0.055;
            g = g <= 0.0031308 ? 12.92 * g : (1.0 + 0.055) * Math.Pow(g, (1.0 / 2.4)) - 0.055;
            b = b <= 0.0031308 ? 12.92 * b : (1.0 + 0.055) * Math.Pow(b, (1.0 / 2.4)) - 0.055;

            return new RGB()
            {
                Red = r,
                Green = g,
                Blue = b,
                Brightness = brightness
            };
        }
    }

    public class HueMirekSchema
    {
        public int mirek_maximum { get; set; }
        public int mirek_minimum { get; set; }
    }

    public class HueColorTemperature
    {
        public int? mirek { get; set; }
        public HueMirekSchema mirek_schema { get; set; }
        public bool? mirek_valid { get; set; }
    }

    public class HueDimming
    {
        public double brightness { get; set; }
        public double min_dim_level { get; set; }
    }

    public class HueDynamics
    {
        public double speed { get; set; }
        public bool speed_valid { get; set; }
        public string status { get; set; }
        public List<string> status_values { get; set; }
    }

    public class HueEffects
    {
        public List<string> effect_values { get; set; }
        public string status { get; set; }
        public List<string> status_values { get; set; }
    }

    public class HueMetadata
    {
        public string archetype { get; set; }
        public string name { get; set; }
    }

    public class HueOn
    {
        public bool on { get; set; }
    }

    public class HueOwner
    {
        public string rid { get; set; }
        public string rtype { get; set; }
    }

    public class HueData
    {
        public HueAlert alert { get; set; }
        public HueColor color { get; set; }
        public HueColorTemperature color_temperature { get; set; }
        public HueDimming dimming { get; set; }
        public HueDynamics dynamics { get; set; }
        public HueEffects effects { get; set; }
        public string id { get; set; }
        public string id_v1 { get; set; }
        public HueMetadata metadata { get; set; }
        public string mode { get; set; }
        public HueOn on { get; set; }
        public HueOwner owner { get; set; }
        public string type { get; set; }
    }

    public class HueLights
    {
        public List<HueError> errors { get; set; }
        public List<HueData> data { get; set; }

    }

    //Custom class
    public class HueLightCommand
    {
        HueLightCommand() { }
        public HueLightCommand(HueData data)
        {
            if (data == null)
                return;

            State = data.on?.on;
            if(data.dimming != null)
                Dimming = data.dimming.brightness;

            if (data.color?.xy != null && data.dimming != null)
            {
                Color = data.color.ConvertToRGB(data.dimming.brightness);
                Color.ToHueSaturation(out double hue, out double saturation);
                Hue = hue;
                Saturation = saturation;
            }

            if (data.color_temperature != null && data.color_temperature.mirek_valid != false)
                ColorTemperature = data.color_temperature.mirek;
        }

        public bool? State { get; set; }
        public RGB Color { get; set; }
        public double? Dimming { get; set; }

        /// <summary>
        /// Hue of the light in degrees (0-360). An alternative to <see cref="Color"/> for callers that
        /// think in hue/saturation, such as the Matter Color Control cluster.
        /// </summary>
        public double? Hue { get; set; }

        /// <summary>
        /// Saturation of the light in percent (0-100). Used together with <see cref="Hue"/>.
        /// </summary>
        public double? Saturation { get; set; }

        /// <summary>
        /// Colour temperature in mireds, which is the unit the Hue bridge itself uses (mirek).
        /// </summary>
        public int? ColorTemperature { get; set; }

        public HueData GetCommand()
        {
            var color = GetColorCommand();

            return new HueData()
            {
                on = GetOnCommand(),
                dimming = GetDimmingCommand(),
                color = color,
                //Setting xy and mirek in the same request is ambiguous, so an explicit colour wins.
                color_temperature = color == null ? GetColorTemperatureCommand() : null
            };
        }

        private HueOn GetOnCommand()
        {
            if (!State.HasValue)
                return null;

            return new HueOn()
            {
                on = State.Value
            };
        }

        private HueDimming GetDimmingCommand()
        {
            double? dim = Color != null ? Color.Brightness : Dimming;
            if (!dim.HasValue)
                return null;

            return new HueDimming()
            {
                brightness = dim.Value
            };
        }

        private HueColor GetColorCommand()
        {
            var color = Color;

            if (color == null && (Hue.HasValue || Saturation.HasValue))
                color = RGB.FromHueSaturation(Hue ?? 0, Saturation ?? 100);

            if (color == null)
                return null;

            return new HueColor()
            {
                xy = HueColor.ConvertRGBToHueColor(color.Red, color.Green, color.Blue, out double bri)
            };
        }

        private HueColorTemperature GetColorTemperatureCommand()
        {
            if (!ColorTemperature.HasValue)
                return null;

            return new HueColorTemperature()
            {
                mirek = ColorTemperature.Value
            };
        }
    }

    public class RGB
    {
        public double Red { get; set; }
        public double Green { get; set; }
        public double Blue { get; set; }
        public double Brightness { get; set; }

        /// <summary>
        /// Builds a fully bright colour from hue in degrees (0-360) and saturation in percent (0-100).
        /// </summary>
        public static RGB FromHueSaturation(double hue, double saturation)
        {
            var h = ((hue % 360) + 360) % 360;
            var s = Math.Min(Math.Max(saturation, 0), 100) / 100;

            var c = s;
            var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
            var m = 1 - c;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return new RGB()
            {
                Red = r + m,
                Green = g + m,
                Blue = b + m
            };
        }

        /// <summary>
        /// Converts this colour to hue in degrees (0-360) and saturation in percent (0-100).
        /// </summary>
        public void ToHueSaturation(out double hue, out double saturation)
        {
            var max = Math.Max(Red, Math.Max(Green, Blue));
            var min = Math.Min(Red, Math.Min(Green, Blue));
            var delta = max - min;

            if (delta <= 0 || max <= 0)
            {
                hue = 0;
                saturation = 0;
                return;
            }

            if (max == Red)
                hue = 60 * (((Green - Blue) / delta) % 6);
            else if (max == Green)
                hue = 60 * (((Blue - Red) / delta) + 2);
            else
                hue = 60 * (((Red - Green) / delta) + 4);

            if (hue < 0)
                hue += 360;

            saturation = (delta / max) * 100;
        }
    }

    public class HueEvent
    {
        public DateTime creationtime { get; set; }
        public List<HueData> data { get; set; }
        public string id { get; set; }
        public string type { get; set; }
    }
    public class HueError
    {
        public string description { get; set; }
    }

}
