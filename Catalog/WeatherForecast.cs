using System.Globalization;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using ValueType = RIoT2.Core.ValueType;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="WeatherForecast"/> device's parameters.</summary>
    public sealed class WeatherForecastOptions
    {
        public const string OpenMeteo = "openMeteo";
        public const string Fmi = "fmi";

        [DeviceParameter("provider", Default = OpenMeteo, Description = "openMeteo (Open-Meteo, worldwide) or fmi (Finnish Meteorological Institute open data, the Nordic countries)")]
        public string Provider { get; set; } = OpenMeteo;

        [DeviceParameter("latitude", Description = "Latitude in degrees, north positive; with fmi, or use fmiPlace")]
        public string Latitude { get; set; } = "";

        [DeviceParameter("longitude", Description = "Longitude in degrees, east positive; with fmi, or use fmiPlace")]
        public string Longitude { get; set; } = "";

        [DeviceParameter("fmiPlace", Description = "With fmi: a place name instead of the coordinates, for example helsinki")]
        public string FmiPlace { get; set; } = "";

        [DeviceParameter("forecastHours", Default = "48", Description = "Hours ahead, 1-240")]
        public int ForecastHours { get; set; } = 48;

        [DeviceParameter("endpoint", Description = "The provider's API URL; empty for its public API")]
        public string Endpoint { get; set; } = "";
    }

    /// <summary>
    /// A weather forecast from Open-Meteo or FMI open data: every hour, each configured report (<c>temperature</c>,
    /// <c>precipitation</c>, <c>windSpeed</c>, <c>cloudCover</c>, <c>solarRadiation</c>, <c>humidity</c>) gets the hourly
    /// forecast as a <see cref="ForecastSeries"/>, from the current hour on.
    /// </summary>
    [RequiresHardware("Weather forecast API")]
    public class WeatherForecast : AsyncDeviceBase<WeatherForecastOptions>, IDeviceWithConfiguration
    {
        public const int MaxForecastHours = 240;

        private readonly Dictionary<string, string> _published = [];
        private IWeatherProvider _provider = new OpenMeteoProvider();
        private double? _latitude;
        private double? _longitude;
        private string? _place;
        private int _hours = 48;
        private string? _endpoint;

        public WeatherForecast(ILogger logger) : base(logger) { }

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        public override void ConfigureDevice()
        {
            _provider = Options.Provider?.Trim() switch
            {
                WeatherForecastOptions.Fmi => new FmiProvider(),
                _ => new OpenMeteoProvider()
            };
            _latitude = Degrees(Options.Latitude);
            _longitude = Degrees(Options.Longitude);
            _place = string.IsNullOrWhiteSpace(Options.FmiPlace) ? null : Options.FmiPlace.Trim();
            _hours = Options.ForecastHours;
            _endpoint = string.IsNullOrWhiteSpace(Options.Endpoint) ? null : Options.Endpoint.Trim();
        }

        protected override Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _published.Clear();
            var problem = Problem();
            if (problem != null)
                throw new InvalidOperationException(problem);
            return SendForecastAsync(cancellationToken);
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _published.Clear();
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : SendForecastAsync(cancellationToken);

        // The HTTP call; the tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            RIoT2.Core.Utils.Web.SendAsync(request, cancellationToken: cancellationToken);

        private string? Problem()
        {
            if (Options.Provider?.Trim() is not (WeatherForecastOptions.OpenMeteo or WeatherForecastOptions.Fmi))
                return $"The provider must be {WeatherForecastOptions.OpenMeteo} or {WeatherForecastOptions.Fmi}.";
            if (!string.IsNullOrWhiteSpace(Options.Latitude) && (_latitude == null || _latitude is < -90 or > 90))
                return "The latitude must be a number from -90 to 90.";
            if (!string.IsNullOrWhiteSpace(Options.Longitude) && (_longitude == null || _longitude is < -180 or > 180))
                return "The longitude must be a number from -180 to 180.";
            var hasCoordinates = _latitude != null && _longitude != null;
            if (_provider is OpenMeteoProvider && !hasCoordinates)
                return "Open-Meteo needs the latitude and longitude.";
            if (_provider is FmiProvider && !hasCoordinates && _place == null)
                return "FMI needs the latitude and longitude, or fmiPlace.";
            if (_hours is < 1 or > MaxForecastHours)
                return $"The forecast hours must be 1-{MaxForecastHours}.";
            return null;
        }

        private async Task SendForecastAsync(CancellationToken cancellationToken)
        {
            var templates = (ReportTemplates ?? []).Where(x => WeatherQuantity.Find(x.Address) != null).ToList();
            if (templates.Count == 0)
                return;

            var now = Time.GetUtcNow();
            var from = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var quantities = templates.Select(x => WeatherQuantity.Find(x.Address)!).Distinct().ToList();
            var query = new WeatherQuery(_latitude, _longitude, _place, from, now.AddHours(_hours), quantities, _endpoint);
            var forecast = await _provider.GetAsync(query, SendAsync, cancellationToken).ConfigureAwait(false);

            foreach (var template in templates)
            {
                if (!forecast.TryGetValue(template.Address, out var points) || points.Count == 0)
                {
                    Logger.LogWarning("The weather forecast has no {Address}", template.Address);
                    continue;
                }
                var quantity = WeatherQuantity.Find(template.Address)!;
                var series = new ForecastSeries
                {
                    IssuedAt = now,
                    Kind = ForecastSeries.ForecastKind,
                    Unit = quantity.Unit,
                    Resolution = "PT1H",
                    Points = points
                };
                var key = string.Join(";", points.Select(x => $"{x.Start.UtcTicks}:{x.Value?.ToString(CultureInfo.InvariantCulture)}"));
                if (_published.TryGetValue(template.Id, out var previous) && previous == key)
                    continue;
                SendReport(this, new Report
                {
                    Id = template.Id,
                    TimeStamp = now.ToUnixTimeSeconds(),
                    Value = series.ToValue(),
                    Filter = ""
                });
                _published[template.Id] = key;
            }
        }

        /// <summary>The points from the query's first hour until its end.</summary>
        internal static List<ForecastPoint> Trim(List<ForecastPoint> points, WeatherQuery query) =>
            points.Where(x => x.End > query.From && x.Start < query.To).ToList();

        private static double? Degrees(string? text) =>
            double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

        public DeviceConfiguration GetConfigurationTemplate()
        {
            return new DeviceConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Weather Forecast",
                RefreshSchedule = "0 5 * ? * *", // every hour, five minutes past
                ClassFullName = GetType().FullName!,
                DeviceParameters = new Dictionary<string, string>
                {
                    ["provider"] = WeatherForecastOptions.OpenMeteo,
                    ["latitude"] = "60.17",
                    ["longitude"] = "24.94",
                    ["fmiPlace"] = "",
                    ["forecastHours"] = "48",
                    ["endpoint"] = ""
                },
                ReportTemplates = WeatherQuantity.All.Select(x => new ReportTemplate
                {
                    Id = Guid.NewGuid().ToString(),
                    Address = x.Address,
                    Name = x.Name + " forecast",
                    Type = ValueType.Entity,
                    Parameters = new Dictionary<string, string> { ["unit"] = x.Unit },
                    Schema = ValueSchema.Forecast(new ValueSchema { Type = "number", Unit = x.Unit, Semantic = x.Semantic, Title = x.Name })
                }).ToList(),
                CommandTemplates = null
            };
        }
    }
}
