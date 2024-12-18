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
        public bool mirek_valid { get; set; }
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

            State = data.on.on;
            if(data.dimming != null)
                Dimming = data.dimming.brightness;

            if (data.color != null)
                Color = data.color.ConvertToRGB(data.dimming.brightness);
        }

        public bool? State { get; set; }
        public RGB Color { get; set; }
        public double? Dimming { get; set; }
        public HueData GetCommand()
        {
            return new HueData()
            {
                on = GetOnCommand(),
                dimming = GetDimmingCommand(),
                color = GetColorCommand()
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
            if(Color == null)
                return null;

            return new HueColor()
            {
                xy = HueColor.ConvertRGBToHueColor(Color.Red, Color.Green, Color.Blue, out double bri)
            };
        }
    }

    public class RGB
    {
        public double Red { get; set; }
        public double Green { get; set; }
        public double Blue { get; set; }    
        public double Brightness { get; set; }
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
