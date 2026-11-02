using RIoT2.Core.Plugins;
using RIoT2.Net.Devices.Models;
using System.Xml.Serialization;
using System.Xml;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core;
using ValueType = RIoT2.Core.ValueType;
using Microsoft.Extensions.Logging;

namespace RIoT2.Net.Devices.Catalog
{
    [RequiresHardware("ENTSO-E API")]
    public class ElectricityPrice : AsyncDeviceBase, IDeviceWithConfiguration
    {
        public ElectricityPrice(ILogger logger) : base(logger) { }

        private string? _securityToken = "";
        private string? _domain = "";
        private string? _endpoint = "";
        private double _vat = 0.0d;
        private const string _priceDataFile = "Data/priceData.xml";
        Publication_MarketDocument? _priceData;

        public override void ConfigureDevice() 
        {
            _securityToken = GetConfiguration<string>("securityToken");
            _domain = GetConfiguration<string>("domain");
            _endpoint = GetConfiguration<string>("endpoint");
            _vat = GetConfiguration<double>("vat");
        }

        protected override async Task StartDeviceAsync(CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            await SendPriceAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override Task StopDeviceAsync(CancellationToken cancellationToken)
        {
            _priceData = null;
            return Task.CompletedTask;
        }

        // Called by the scheduler; the refresh is defined on the device, so the report is null.
        protected override Task RefreshAsync(ReportTemplate? report, CancellationToken cancellationToken) =>
            report != null ? Task.CompletedTask : SendPriceAsync(cancellationToken);

        private async Task SendPriceAsync(CancellationToken cancellationToken)
        {
            var template = getPriceReportTemplate();
            if (template == null)
                return;

            var price = await GetCurrentPriceAsync(template, cancellationToken).ConfigureAwait(false);
            SendReport(this, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(price),
                Filter = ""
            });
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
                Address = "price",
                Name = "Price",
                Type = ValueType.Number,
                Parameters = new Dictionary<string, string>() { { "unit", "c/kWh" }, { "precision", "2" } }
            });

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = null; //set explicitly to null
            return deviceConfiguration;
        }

        private ReportTemplate? getPriceReportTemplate() 
        {
            return ReportTemplates?.FirstOrDefault(x => x.Address == "price");
        }

        private async Task<decimal> GetCurrentPriceAsync(ReportTemplate reportTemplate, CancellationToken cancellationToken)
        {
            if (!isPriceDataValid(_priceData))
                await LoadAsync(cancellationToken).ConfigureAwait(false);

            if (_priceData == null)
                return 0m;

            //check resolution
            var resStr = _priceData.TimeSeries.Period.resolution.Remove(0, 2);
            resStr = resStr.Remove(resStr.Length - 1);
            int res = int.Parse(resStr);

            //get current price position based on resolution
            var p = (DateTime.Now.Hour - 1);
            if(p < 0)
                p = 0;

            var minutesFromMidnight = p * 60 + DateTime.Now.Minute + 1;
            
            int pos = (int)Math.Ceiling((double)minutesFromMidnight / res);

            var point = _priceData.TimeSeries.Period.Point.FirstOrDefault(x => x.position == pos);
            if (point == null)
                return 0m;

            var rawPrice = point.priceamount;

            if (_vat != 0.0d) //Add VAT
            {
                var vatPercentage = decimal.Divide((decimal)_vat, 100);
                vatPercentage += 1.0M;
                rawPrice = decimal.Multiply(rawPrice, vatPercentage);
            }

            //divice by ten to get price as c/kWh
            if (rawPrice != 0m)
                rawPrice = decimal.Divide(rawPrice, 10);

            int precision = 2; //default
            if (reportTemplate != null && reportTemplate.Parameters != null && reportTemplate.Parameters.ContainsKey("precision"))
                precision = int.Parse(reportTemplate.Parameters["precision"]);

            if (precision > -1)
                return Math.Round(rawPrice, precision);

            return rawPrice;
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(_priceDataFile)) 
                {
                    await getPriceDataForCurrentDay(cancellationToken).ConfigureAwait(false);
                    return;
                }
                    
                var xml = await File.ReadAllTextAsync(_priceDataFile, cancellationToken).ConfigureAwait(false);

                XmlSerializer serializer = new XmlSerializer(typeof(Publication_MarketDocument));
                using (var reader = new StringReader(xml))
                {
                    var filePriceData = (Publication_MarketDocument?)serializer.Deserialize(new NamespaceIgnorantXmlTextReader(reader));
                    if (isPriceDataValid(filePriceData))
                        _priceData = filePriceData;
                    else
                        await getPriceDataForCurrentDay(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch(Exception x)
            {
                throw new Exception("Could not load Electricity Prices", x);
            }
        }

        private bool isPriceDataValid(Publication_MarketDocument? priceData)
        {
            if (priceData == null)
                return false;

            return (DateTime.Now > parseDate(priceData.TimeSeries.Period.timeInterval.start).AddHours(2) &&
                DateTime.Now < parseDate(priceData.TimeSeries.Period.timeInterval.end).AddHours(2));
        }

        private DateTime parseDate(string s) 
        {
            return DateTime.Parse(s);
            //2022-11-17T15:37:49Z
        }

        private void save()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Publication_MarketDocument));
                using (TextWriter writer = new StreamWriter(_priceDataFile, false)) 
                {
                    serializer.Serialize(writer, _priceData);
                    writer.Close();
                }
            }
            catch(Exception x)
            {
                Logger.LogError(x, $"Could not save Electricity Prices file {_priceDataFile}");
            }
        }

        private async Task getPriceDataForCurrentDay(CancellationToken cancellationToken)
        {
            try
            {
                var now = DateTime.Now.ToString("yyyyMMdd"); //yyyyMMddHHmm
                var url = _endpoint + $"?securityToken={_securityToken}&documentType=A44&in_Domain={_domain}&out_Domain={_domain}&periodStart={now + "0000"}&periodEnd={now + "2300"}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                XmlSerializer serializer = new XmlSerializer(typeof(Publication_MarketDocument));
                using (StringReader reader = new StringReader(xml))
                {
                    _priceData = (Publication_MarketDocument?)serializer.Deserialize(new NamespaceIgnorantXmlTextReader(reader));
                    save();
                }
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
    }

    internal class NamespaceIgnorantXmlTextReader : XmlTextReader
    {
        public NamespaceIgnorantXmlTextReader(System.IO.TextReader reader) : base(reader) { }

        public override string NamespaceURI
        {
            get { return ""; }
        }
    }
}
