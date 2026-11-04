using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using RIoT2.Core.Models;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>A forecast value the <see cref="WeatherForecast"/> device reports, by its report address.</summary>
    /// <param name="Address">The report template address.</param>
    /// <param name="OpenMeteo">The Open-Meteo hourly variable.</param>
    /// <param name="Fmi">The FMI open data parameter.</param>
    /// <param name="FmiQuery">The FMI stored query that has the parameter.</param>
    /// <param name="PrecedingHour">The value at a time is the amount or mean of the hour before it, not the moment.</param>
    internal sealed record WeatherQuantity(string Address, string Name, string Unit, string? Semantic, string OpenMeteo, string Fmi, string FmiQuery,
        bool OpenMeteoPrecedingHour, bool FmiPrecedingHour)
    {
        public const string FmiEdited = "fmi::forecast::edited::weather::scandinavia::point::simple";
        public const string FmiHarmonie = "fmi::forecast::harmonie::surface::point::simple";

        public static readonly IReadOnlyList<WeatherQuantity> All =
        [
            new("temperature", "Temperature", "°C", ValueSemantics.Temperature, "temperature_2m", "Temperature", FmiEdited, false, false),
            new("precipitation", "Precipitation", "mm", "precipitation", "precipitation", "Precipitation1h", FmiEdited, true, true),
            new("windSpeed", "Wind speed", "m/s", "windSpeed", "wind_speed_10m", "WindSpeedMS", FmiEdited, false, false),
            new("cloudCover", "Cloud cover", "%", "cloudCover", "cloud_cover", "TotalCloudCover", FmiEdited, false, false),
            new("solarRadiation", "Solar radiation", "W/m²", "solarRadiation", "shortwave_radiation", "RadiationGlobal", FmiHarmonie, true, false),
            new("humidity", "Humidity", "%", ValueSemantics.Humidity, "relative_humidity_2m", "Humidity", FmiEdited, false, false)
        ];

        public static WeatherQuantity? Find(string? address) => All.FirstOrDefault(x => x.Address == address);
    }

    /// <summary>What a provider is asked for: a place, the forecast's end, and the quantities.</summary>
    internal sealed record WeatherQuery(double? Latitude, double? Longitude, string? Place, DateTimeOffset From, DateTimeOffset To,
        IReadOnlyList<WeatherQuantity> Quantities, string? Endpoint);

    /// <summary>Sends a provider's HTTP request: the device's <c>SendAsync</c>, which tests replace.</summary>
    internal delegate Task<HttpResponseMessage> WeatherHttp(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>A forecast source: hourly points per report address.</summary>
    internal interface IWeatherProvider
    {
        Task<Dictionary<string, List<ForecastPoint>>> GetAsync(WeatherQuery query, WeatherHttp http, CancellationToken cancellationToken);
    }

    /// <summary>Open-Meteo's free forecast API (<c>/v1/forecast</c>, hourly, no key, up to 16 days).</summary>
    internal sealed class OpenMeteoProvider : IWeatherProvider
    {
        public const string DefaultEndpoint = "https://api.open-meteo.com/v1/forecast";

        public static string Url(WeatherQuery query)
        {
            if (query.Latitude == null || query.Longitude == null)
                throw new InvalidOperationException("Open-Meteo needs the latitude and longitude.");
            var days = Math.Min(16, (int)Math.Ceiling((query.To - query.From.UtcDateTime.Date).TotalHours / 24) + 1);
            var hourly = string.Join(",", query.Quantities.Select(x => x.OpenMeteo));
            return (string.IsNullOrWhiteSpace(query.Endpoint) ? DefaultEndpoint : query.Endpoint) +
                $"?latitude={query.Latitude.Value.ToString(CultureInfo.InvariantCulture)}&longitude={query.Longitude.Value.ToString(CultureInfo.InvariantCulture)}" +
                $"&hourly={hourly}&forecast_days={days}&timezone=GMT&wind_speed_unit=ms&timeformat=unixtime";
        }

        public async Task<Dictionary<string, List<ForecastPoint>>> GetAsync(WeatherQuery query, WeatherHttp http, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(query));
            using var response = await http(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Open-Meteo answered {(int)response.StatusCode}: {json}");
            return Parse(json, query);
        }

        public static Dictionary<string, List<ForecastPoint>> Parse(string json, WeatherQuery query)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("hourly", out var hourly) || !hourly.TryGetProperty("time", out var times))
                throw new InvalidOperationException("Open-Meteo sent no hourly forecast.");
            var starts = times.EnumerateArray().Select(x => DateTimeOffset.FromUnixTimeSeconds(x.GetInt64())).ToList();
            var result = new Dictionary<string, List<ForecastPoint>>();
            foreach (var quantity in query.Quantities)
            {
                if (!hourly.TryGetProperty(quantity.OpenMeteo, out var values))
                    continue;
                var points = new List<ForecastPoint>();
                var i = 0;
                foreach (var value in values.EnumerateArray())
                {
                    if (i >= starts.Count)
                        break;
                    var time = starts[i++];
                    var start = quantity.OpenMeteoPrecedingHour ? time.AddHours(-1) : time;
                    points.Add(new ForecastPoint(start, start.AddHours(1), value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null));
                }
                result[quantity.Address] = WeatherForecast.Trim(points, query);
            }
            return result;
        }
    }

    /// <summary>
    /// The Finnish Meteorological Institute's open data (WFS simple features): the edited forecast for most values, and
    /// HARMONIE for solar radiation, which the edited forecast doesn't have.
    /// </summary>
    internal sealed class FmiProvider : IWeatherProvider
    {
        public const string DefaultEndpoint = "https://opendata.fmi.fi/wfs";

        public static string Url(WeatherQuery query, string storedQuery, IEnumerable<WeatherQuantity> quantities)
        {
            string place;
            if (!string.IsNullOrWhiteSpace(query.Place))
                place = "place=" + Uri.EscapeDataString(query.Place.Trim());
            else if (query.Latitude != null && query.Longitude != null)
                place = $"latlon={query.Latitude.Value.ToString(CultureInfo.InvariantCulture)},{query.Longitude.Value.ToString(CultureInfo.InvariantCulture)}";
            else
                throw new InvalidOperationException("FMI needs the place or the latitude and longitude.");
            return (string.IsNullOrWhiteSpace(query.Endpoint) ? DefaultEndpoint : query.Endpoint) +
                $"?service=WFS&version=2.0.0&request=getFeature&storedquery_id={storedQuery}&{place}" +
                $"&parameters={string.Join(",", quantities.Select(x => x.Fmi))}&timestep=60" +
                $"&starttime={query.From.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&endtime={query.To.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        }

        public async Task<Dictionary<string, List<ForecastPoint>>> GetAsync(WeatherQuery query, WeatherHttp http, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<ForecastPoint>>();
            foreach (var group in query.Quantities.GroupBy(x => x.FmiQuery))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url(query, group.Key, group));
                using var response = await http(request, cancellationToken).ConfigureAwait(false);
                var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"FMI answered {(int)response.StatusCode}: {Exception(xml) ?? xml}");
                foreach (var pair in Parse(xml, query, group.ToList()))
                    result[pair.Key] = pair.Value;
            }
            return result;
        }

        public static Dictionary<string, List<ForecastPoint>> Parse(string xml, WeatherQuery query, IReadOnlyList<WeatherQuantity> quantities)
        {
            var document = XDocument.Parse(xml);
            var values = new Dictionary<string, List<ForecastPoint>>();
            foreach (var element in document.Descendants().Where(x => x.Name.LocalName == "BsWfsElement"))
            {
                var name = Child(element, "ParameterName");
                var quantity = quantities.FirstOrDefault(x => x.Fmi == name);
                var timeText = Child(element, "Time");
                if (quantity == null || timeText == null)
                    continue;
                var time = DateTimeOffset.Parse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var start = quantity.FmiPrecedingHour ? time.AddHours(-1) : time;
                double? value = double.TryParse(Child(element, "ParameterValue"), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    && !double.IsNaN(number) ? number : null;
                if (!values.TryGetValue(quantity.Address, out var points))
                    values[quantity.Address] = points = [];
                points.Add(new ForecastPoint(start, start.AddHours(1), value));
            }
            return values.ToDictionary(x => x.Key, x => WeatherForecast.Trim(x.Value.OrderBy(p => p.Start).ToList(), query));
        }

        private static string? Exception(string xml)
        {
            try
            {
                return XDocument.Parse(xml).Descendants().FirstOrDefault(x => x.Name.LocalName == "ExceptionText")?.Value;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        private static string? Child(XElement element, string name) =>
            element.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
    }
}
