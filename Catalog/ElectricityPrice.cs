using RIoT2.Core.Plugins;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core;
using ValueType = RIoT2.Core.ValueType;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>The <see cref="ElectricityPrice"/> device's parameters: the ENTSO-E transparency platform.</summary>
    public sealed class ElectricityPriceOptions
    {
        [DeviceParameter("securityToken", Required = true, Secret = true, Description = "ENTSO-E API security token")]
        public string SecurityToken { get; set; } = "";

        [DeviceParameter("domain", Default = "10YFI-1--------U", Description = "Bidding zone (EIC code)")]
        public string Domain { get; set; } = "";

        [DeviceParameter("endpoint", Default = "https://web-api.tp.entsoe.eu/api", Description = "ENTSO-E API URL")]
        public string Endpoint { get; set; } = "";

        [DeviceParameter("vat", Description = "VAT percent added to the price")]
        public double Vat { get; set; }
    }

    /// <summary>
    /// Day-ahead electricity prices from ENTSO-E: the current price (<c>price</c>) on every refresh, and today's and,
    /// once published (around 12:00 UTC), tomorrow's prices as a forecast series (<c>prices</c>) whenever they change.
    /// </summary>
    [RequiresHardware("ENTSO-E API")]
    public class ElectricityPrice : AsyncDeviceBase<ElectricityPriceOptions>, IDeviceWithConfiguration
    {
        public ElectricityPrice(ILogger logger) : base(logger) { }

        internal const string PriceAddress = "price";
        internal const string PricesAddress = "prices";
        internal const string PriceDataFile = "Data/priceData.json";

        // Tomorrow's prices are due when today's run out within this time; asked again at most every RetryInterval.
        internal static readonly TimeSpan TomorrowDue = TimeSpan.FromHours(13);
        internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

        private string? _securityToken = "";
        private string? _domain = "";
        private string? _endpoint = "";
        private double _vat = 0.0d;
        private List<PriceSlot> _prices = [];
        private DateTimeOffset _issuedAt;
        private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
        private string? _publishedPrices;

        /// <summary>The clock; tests replace it.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        /// <summary>The time zone whose days the <c>prices</c> series starts at; tests replace it.</summary>
        internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

        /// <summary>Where the prices are kept between starts; tests replace it.</summary>
        internal string DataFile { get; set; } = PriceDataFile;

        public override void ConfigureDevice() 
        {
            _securityToken = Options.SecurityToken;
            _domain = Options.Domain;
            _endpoint = Options.Endpoint;
            _vat = Options.Vat;
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            _publishedPrices = null;
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SendPricesAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _prices = [];
            _lastAttempt = DateTimeOffset.MinValue;
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : SendPricesAsync(cancellationToken);

        private async Task SendPricesAsync(CancellationToken cancellationToken)
        {
            await UpdateAsync(cancellationToken).ConfigureAwait(false);

            var priceTemplate = GetTemplate(PriceAddress);
            if (priceTemplate != null)
            {
                SendReport(this, new Report()
                {
                    Id = priceTemplate.Id,
                    TimeStamp = Time.GetUtcNow().ToUnixTimeSeconds(),
                    Value = new ValueModel(CurrentPrice(priceTemplate)),
                    Filter = ""
                });
            }

            var seriesTemplate = GetTemplate(PricesAddress);
            if (seriesTemplate != null && _prices.Count > 0)
            {
                var series = Series(seriesTemplate);
                var key = string.Join(";", series.Points.Select(x => $"{x.Start.UtcTicks}:{x.Value}"));
                if (key != _publishedPrices)
                {
                    SendReport(this, new Report()
                    {
                        Id = seriesTemplate.Id,
                        TimeStamp = Time.GetUtcNow().ToUnixTimeSeconds(),
                        Value = series.ToValue(),
                        Filter = ""
                    });
                    _publishedPrices = key;
                }
            }
        }

        // The HTTP call; the lifecycle tests replace it.
        internal virtual Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            RIoT2.Core.Utils.Web.SendAsync(request, cancellationToken: cancellationToken);

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Electricity Price";
            deviceConfiguration.RefreshSchedule = "0 */15 * ? * *"; //every 15min
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("securityToken", Guid.NewGuid().ToString());
            deviceConfiguration.DeviceParameters.Add("domain", "10YFI-1--------U");
            deviceConfiguration.DeviceParameters.Add("endpoint", "https://web-api.tp.entsoe.eu/api");
            deviceConfiguration.DeviceParameters.Add("vat", "25.5");

            deviceConfiguration.ClassFullName = this.GetType().FullName!;
            var reportConfigurations = new List<ReportTemplate>();

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = PriceAddress,
                Name = "Price",
                Type = ValueType.Number,
                Parameters = new Dictionary<string, string>() { { "unit", "c/kWh" }, { "precision", "2" } }
            });

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = PricesAddress,
                Name = "Prices today and tomorrow",
                Type = ValueType.Entity,
                Parameters = new Dictionary<string, string>() { { "unit", "c/kWh" }, { "precision", "2" } },
                Schema = ValueSchema.Forecast(new ValueSchema { Type = "number", Unit = "c/kWh", Title = "Price" })
            });

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = null; //set explicitly to null
            return deviceConfiguration;
        }

        private ReportTemplate? GetTemplate(string address) => ReportTemplates?.FirstOrDefault(x => x.Address == address);

        private decimal CurrentPrice(ReportTemplate template)
        {
            var now = Time.GetUtcNow();
            var slot = _prices.FirstOrDefault(x => x.Start <= now && now < x.End);
            return slot == null ? 0m : Convert(slot.Price, template);
        }

        /// <summary>The prices from local midnight today on, in c/kWh with VAT.</summary>
        private ForecastSeries Series(ReportTemplate template)
        {
            var today = LocalMidnight(Time.GetUtcNow());
            var points = _prices.Where(x => x.End > today).ToList();
            var resolution = points.Count == 0 ? null : System.Xml.XmlConvert.ToString(points.Min(x => x.End - x.Start));
            return new ForecastSeries
            {
                IssuedAt = _issuedAt,
                Kind = ForecastSeries.ConfirmedKind,
                Unit = Unit(template),
                Resolution = resolution,
                Points = points.Select(x => new ForecastPoint(x.Start, x.End, (double)Convert(x.Price, template))).ToList()
            };
        }

        private DateTimeOffset LocalMidnight(DateTimeOffset now)
        {
            var local = TimeZoneInfo.ConvertTime(now, Zone).Date;
            while (Zone.IsInvalidTime(local))
                local = local.AddMinutes(30);
            return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
        }

        private static string Unit(ReportTemplate template) =>
            template.Parameters != null && template.Parameters.TryGetValue("unit", out var unit) && !string.IsNullOrWhiteSpace(unit) ? unit : "c/kWh";

        /// <summary>EUR/MWh to c/kWh, with VAT, rounded to the template's precision (2 by default, -1 for none).</summary>
        private decimal Convert(decimal rawPrice, ReportTemplate template)
        {
            if (_vat != 0.0d) //Add VAT
            {
                var vatPercentage = decimal.Divide((decimal)_vat, 100);
                vatPercentage += 1.0M;
                rawPrice = decimal.Multiply(rawPrice, vatPercentage);
            }

            //divide by ten to get price as c/kWh
            if (rawPrice != 0m)
                rawPrice = decimal.Divide(rawPrice, 10);

            int precision = 2; //default
            if (template.Parameters != null && template.Parameters.TryGetValue("precision", out var text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                precision = parsed;

            if (precision > -1)
                return Math.Round(rawPrice, precision);

            return rawPrice;
        }

        private bool Covers(DateTimeOffset now) => _prices.Any(x => x.Start <= now && now < x.End);

        /// <summary>Downloads when the current price is missing, or when tomorrow's prices are due and not yet here.</summary>
        private async Task UpdateAsync(CancellationToken cancellationToken)
        {
            var now = Time.GetUtcNow();
            if (Covers(now))
            {
                var remaining = _prices.Max(x => x.End) - now;
                if (remaining >= TomorrowDue || now - _lastAttempt < RetryInterval)
                    return;
                try
                {
                    await DownloadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception x)
                {
                    // Today's prices are still here: keep them and try again later.
                    Logger.LogWarning(x, "Could not load tomorrow's electricity prices");
                }
                return;
            }
            await DownloadAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (File.Exists(DataFile))
                {
                    var cache = PriceCache.Read(await File.ReadAllTextAsync(DataFile, cancellationToken).ConfigureAwait(false));
                    if (cache != null && cache.Domain == _domain && cache.Slots.Count > 0)
                    {
                        _prices = cache.Slots.OrderBy(x => x.Start).ToList();
                        _issuedAt = cache.FetchedAt;
                    }
                }
                await UpdateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x)
            {
                throw new Exception("Could not load Electricity Prices", x);
            }
        }

        private async Task DownloadAsync(CancellationToken cancellationToken)
        {
            var now = Time.GetUtcNow();
            _lastAttempt = now;
            // Yesterday to the day after tomorrow in UTC covers today and tomorrow in every European zone.
            var from = now.UtcDateTime.Date.AddDays(-1);
            var to = now.UtcDateTime.Date.AddDays(2);
            var url = _endpoint + $"?securityToken={_securityToken}&documentType=A44&in_Domain={_domain}&out_Domain={_domain}" +
                $"&periodStart={from:yyyyMMddHHmm}&periodEnd={to:yyyyMMddHHmm}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var acknowledgement = EntsoePrices.Acknowledgement(xml);
                if (acknowledgement != null)
                    throw new InvalidOperationException($"ENTSO-E sent no prices: {acknowledgement}");
                var prices = EntsoePrices.Parse(xml);
                if (prices.Count == 0)
                    throw new InvalidOperationException("ENTSO-E sent no prices.");

                // Keep yesterday's slots only as long as they are needed for today.
                var keepFrom = LocalMidnight(now).AddDays(-1);
                _prices = prices.Where(x => x.End > keepFrom).ToList();
                _issuedAt = now;
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception x) 
            {
                Logger.LogError(x, "Could not load Electricity Prices from WebAPI");
                throw new Exception("Could not load Electricity Prices from WebAPI", x);
            }
        }

        private async Task SaveAsync(CancellationToken cancellationToken)
        {
            try
            {
                var folder = Path.GetDirectoryName(DataFile);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                var cache = new PriceCache { Domain = _domain ?? "", FetchedAt = _issuedAt, Slots = _prices };
                await File.WriteAllTextAsync(DataFile, cache.Write(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            {
                Logger.LogError(x, "Could not save Electricity Prices file {File}", DataFile);
            }
        }
    }
}
