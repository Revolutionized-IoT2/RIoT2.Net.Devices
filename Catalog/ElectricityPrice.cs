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
    public class ElectricityPrice : DeviceBase, IRefreshableReportDevice, IDeviceWithConfiguration
    {
        public ElectricityPrice(ILogger logger) : base(logger) { }

        private string _securityToken = "";
        private string _domain = "";
        private string _endpoint = "";
        private double _vat = 0.0d;
        private const string _priceDataFile = "Data/priceData.xml";
        Publication_MarketDocument _priceData;

        public override void ConfigureDevice() 
        {
            _securityToken = GetConfiguration<string>("securityToken");
            _domain = GetConfiguration<string>("domain");
            _endpoint = GetConfiguration<string>("endpoint");
            _vat = GetConfiguration<double>("vat");
        }

        public override void StartDevice()
        {
            load();

            var template = getPriceReportTemplate();
            SendReport(this, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(getCurrentPrice()),
                Filter = ""
            });
        }

        public override void StopDevice()
        {
            _priceData = null;
        }

        //This is called by base when time trigger is activated
        public override void Refresh(ReportTemplate report) 
        {
            if (report != null) //report should be null because in this case, refresh is defined on device level
                return;

            var template = getPriceReportTemplate();
            SendReport(this, new Report()
            {
                Id = template.Id,
                TimeStamp = DateTime.UtcNow.ToEpoch(),
                Value = new ValueModel(getCurrentPrice()),
                Filter = ""
            });
        }

        public DeviceConfiguration GetConfigurationTemplate()
        {
            var deviceConfiguration = new DeviceConfiguration();
            deviceConfiguration.Id = Guid.NewGuid().ToString();
            deviceConfiguration.Name = "Electricity Price Provider";
            deviceConfiguration.RefreshSchedule = "*/15 * * * *"; //0 * * * *
            deviceConfiguration.DeviceParameters = new Dictionary<string, string>();
            deviceConfiguration.DeviceParameters.Add("securityToken", Guid.NewGuid().ToString());
            deviceConfiguration.DeviceParameters.Add("domain", "10YFI-1--------U");
            deviceConfiguration.DeviceParameters.Add("endpoint", "https://web-api.tp.entsoe.eu/api");
            deviceConfiguration.DeviceParameters.Add("vat", "25.5");

            deviceConfiguration.ClassFullName = this.GetType().FullName;
            var reportConfigurations = new List<ReportTemplate>();

            reportConfigurations.Add(new ReportTemplate()
            {
                Id = Guid.NewGuid().ToString(),
                Address = "price",
                Name = "Price",
                Type = ValueType.Number
            });

            deviceConfiguration.ReportTemplates = reportConfigurations;
            deviceConfiguration.CommandTemplates = null; //set explicitly to null
            return deviceConfiguration;
        }

        private ReportTemplate getPriceReportTemplate() 
        {
            return ReportTemplates.FirstOrDefault(x => x.Address == "price");
        }

        private decimal getCurrentPrice() 
        {
            if (!isPriceDataValid(_priceData))
                load();

            //check resolution
            var resStr = _priceData.TimeSeries.Period.resolution.Remove(0, 2);
            resStr = resStr.Remove(resStr.Length - 1);
            int res = int.Parse(resStr);

            //get current price position based on resolution
            var p = (DateTime.Now.Hour - 1);
            if(p < 0)
                p = 0;

            var minutesFromMidnight = p * 60 + DateTime.Now.Minute;
            
            int pos = (int)Math.Ceiling((double)minutesFromMidnight / res);

            var rawPrice = _priceData.TimeSeries.Period.Point.FirstOrDefault(x => x.position == pos).priceamount;

            if (_vat != 0.0d) //Add VAT
            {
                var vatPercentage = decimal.Divide((decimal)_vat, 100);
                vatPercentage += 1.0M;
                rawPrice = decimal.Multiply(rawPrice, vatPercentage);
            }

            //divice by ten to get price as c/kWh
            if (rawPrice != 0m)
                rawPrice = decimal.Divide(rawPrice, 10);

            //return value rounded to two digits
            return Math.Round(rawPrice, 2);
        }

        private Dictionary<int, decimal> getCurrentDayPrices()
        {
            if (!isPriceDataValid(_priceData))
                load();

            return _priceData.TimeSeries.Period.Point
                .Select(p => new { id = p.position, key = p.priceamount })
                .ToDictionary(d => d.id, d => d.key);
        }

        private void load()
        {
            try
            {
                if (!File.Exists(_priceDataFile)) 
                {
                    getPriceDataForCurrentDay().Wait();
                    return;
                }
                    
                var xml = File.ReadAllText(_priceDataFile);

                XmlSerializer serializer = new XmlSerializer(typeof(Publication_MarketDocument));
                using (var reader = new StringReader(xml))
                {
                    var filePriceData = (Publication_MarketDocument)serializer.Deserialize(new NamespaceIgnorantXmlTextReader(reader));
                    if (isPriceDataValid(filePriceData))
                        _priceData = filePriceData;
                    else
                        getPriceDataForCurrentDay().Wait();
                }
            }
            catch(Exception x)
            {
                throw new Exception("Could not load Electricity Prices", x);
            }
        }

        private bool isPriceDataValid(Publication_MarketDocument priceData)
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

        private async Task getPriceDataForCurrentDay() 
        {
            try
            {
                var now = DateTime.Now.ToString("yyyyMMdd"); //yyyyMMddHHmm
                var url = _endpoint + $"?securityToken={_securityToken}&documentType=A44&in_Domain={_domain}&out_Domain={_domain}&periodStart={now + "0000"}&periodEnd={now + "2300"}";
                var response = await RIoT2.Core.Utils.Web.GetAsync(url);
                var xml = await response.Content.ReadAsStringAsync();

                XmlSerializer serializer = new XmlSerializer(typeof(Publication_MarketDocument));
                using (StringReader reader = new StringReader(xml))
                {
                    _priceData = (Publication_MarketDocument)serializer.Deserialize(new NamespaceIgnorantXmlTextReader(reader));
                    save();
                }
            }
            catch (Exception x) 
            {
                Logger.LogError(x, $"Could not load Electricity Prices from WebAPI");
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
