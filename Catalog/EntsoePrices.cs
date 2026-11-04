using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace RIoT2.Net.Devices.Catalog
{
    /// <summary>One price slot from an ENTSO-E day-ahead document, in UTC, in EUR/MWh without VAT.</summary>
    internal sealed record PriceSlot(DateTimeOffset Start, DateTimeOffset End, decimal Price);

    /// <summary>
    /// Reads ENTSO-E <c>Publication_MarketDocument</c> (A44) prices: every <c>TimeSeries</c> and <c>Period</c>, curve A01
    /// (every position) and A03 (a missing position repeats the previous price). Where series overlap, as when a zone
    /// publishes both PT60M and PT15M, the finer resolution wins.
    /// </summary>
    internal static class EntsoePrices
    {
        public static List<PriceSlot> Parse(string xml)
        {
            var document = XDocument.Parse(xml);
            var periods = new List<(TimeSpan Resolution, List<PriceSlot> Slots)>();
            foreach (var series in document.Descendants().Where(x => x.Name.LocalName == "TimeSeries"))
            {
                var curve = Child(series, "curveType")?.Value.Trim() ?? "A01";
                foreach (var period in series.Elements().Where(x => x.Name.LocalName == "Period"))
                {
                    var interval = Child(period, "timeInterval");
                    var startText = Child(interval, "start")?.Value;
                    var endText = Child(interval, "end")?.Value;
                    var resolutionText = Child(period, "resolution")?.Value;
                    if (startText == null || endText == null || resolutionText == null)
                        continue;
                    var start = ParseTime(startText);
                    var end = ParseTime(endText);
                    var resolution = XmlConvert.ToTimeSpan(resolutionText.Trim());
                    if (resolution <= TimeSpan.Zero || end <= start)
                        continue;

                    var prices = new Dictionary<int, decimal>();
                    foreach (var point in period.Elements().Where(x => x.Name.LocalName == "Point"))
                    {
                        var position = Child(point, "position")?.Value;
                        var amount = Child(point, "price.amount")?.Value;
                        if (position != null && amount != null)
                            prices[int.Parse(position, CultureInfo.InvariantCulture)] = decimal.Parse(amount, NumberStyles.Float, CultureInfo.InvariantCulture);
                    }

                    var slots = new List<PriceSlot>();
                    var count = (int)((end - start).Ticks / resolution.Ticks);
                    decimal? previous = null;
                    for (var position = 1; position <= count; position++)
                    {
                        decimal? price = prices.TryGetValue(position, out var value) ? value : curve == "A03" ? previous : null;
                        if (price != null)
                            slots.Add(new PriceSlot(start + resolution * (position - 1), start + resolution * position, price.Value));
                        previous = price;
                    }
                    periods.Add((resolution, slots));
                }
            }

            var result = new List<PriceSlot>();
            foreach (var (_, slots) in periods.OrderBy(x => x.Resolution))
                foreach (var slot in slots)
                    if (!result.Any(x => x.Start < slot.End && slot.Start < x.End))
                        result.Add(slot);
            return result.OrderBy(x => x.Start).ToList();
        }

        /// <summary>The reason in an ENTSO-E <c>Acknowledgement_MarketDocument</c>, such as "No matching data found".</summary>
        public static string? Acknowledgement(string xml)
        {
            try
            {
                var document = XDocument.Parse(xml);
                if (document.Root?.Name.LocalName != "Acknowledgement_MarketDocument")
                    return null;
                return document.Descendants().FirstOrDefault(x => x.Name.LocalName == "text")?.Value ?? "The request was not answered with prices.";
            }
            catch (XmlException)
            {
                return null;
            }
        }

        private static XElement? Child(XElement? parent, string name) =>
            parent?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

        private static DateTimeOffset ParseTime(string text) =>
            DateTimeOffset.Parse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>The prices the device keeps in <c>Data/priceData.json</c> between starts.</summary>
    internal sealed class PriceCache
    {
        public string Domain { get; set; } = "";
        public DateTimeOffset FetchedAt { get; set; }
        public List<PriceSlot> Slots { get; set; } = [];

        public static PriceCache? Read(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<PriceCache>(json, Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string Write() => JsonSerializer.Serialize(this, Options);

        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}
