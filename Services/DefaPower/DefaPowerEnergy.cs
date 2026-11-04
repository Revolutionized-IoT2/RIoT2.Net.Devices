using RIoT2.Core.Models;
using RIoT2.Net.Devices.Models;

namespace RIoT2.Net.Devices.Services.DefaPower
{
    /// <summary>What a meter sample did to a connector's energy accounting.</summary>
    internal enum DefaPowerSample
    {
        /// <summary>The first sample: the accounting starts from it.</summary>
        First,
        /// <summary>The energy since the previous sample was shared between the quarters.</summary>
        Added,
        /// <summary>The meter went backwards (replaced or reset); the interval counts as no energy.</summary>
        MeterReset,
        /// <summary>The sample isn't newer than the previous one.</summary>
        Ignored
    }

    /// <summary>
    /// Turns a connector's lifetime meter samples into the energy of each quarter-hour and hour, and works out costs from
    /// electricity prices. The energy between two samples is shared between the quarters they span in proportion to time.
    /// </summary>
    internal static class DefaPowerEnergy
    {
        public static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

        /// <summary>How long the quarters are kept; quarters older than this are no longer reported.</summary>
        public static readonly TimeSpan Keep = TimeSpan.FromHours(48);

        /// <summary>How long the electricity prices are kept.</summary>
        public static readonly TimeSpan KeepPrices = TimeSpan.FromDays(3);

        public static DateTimeOffset QuarterStart(DateTimeOffset time) => Floor(time, Quarter);

        public static DateTimeOffset HourStart(DateTimeOffset time) => Floor(time, Hour);

        private static DateTimeOffset Floor(DateTimeOffset time, TimeSpan length)
        {
            var ticks = time.UtcTicks;
            return new DateTimeOffset(ticks - ticks % length.Ticks, TimeSpan.Zero);
        }

        public static DefaPowerSample AddSample(DefaPowerConnectorState state, DateTimeOffset time, double meter)
        {
            if (state.SampleTime is not DateTimeOffset previousTime || state.SampleMeter is not double previousMeter)
            {
                state.Since ??= time;
                state.SampleTime = time;
                state.SampleMeter = meter;
                return DefaPowerSample.First;
            }
            if (time <= previousTime)
                return DefaPowerSample.Ignored;

            var delta = meter - previousMeter;
            var result = DefaPowerSample.Added;
            if (delta < 0)
            {
                delta = 0;
                result = DefaPowerSample.MeterReset;
            }
            Share(state, previousTime, time, delta);
            state.SampleTime = time;
            state.SampleMeter = meter;
            Prune(state, time);
            return result;
        }

        // Every quarter the interval touches gets its share, a zero one too, so the quarters have no gaps.
        private static void Share(DefaPowerConnectorState state, DateTimeOffset from, DateTimeOffset to, double energy)
        {
            var total = (to - from).TotalSeconds;
            var first = QuarterStart(from);
            var oldest = QuarterStart(to - Keep);
            if (first < oldest)
                first = oldest;
            for (var start = first; start < to; start += Quarter)
            {
                var overlap = (Min(start + Quarter, to) - Max(start, from)).TotalSeconds;
                if (overlap < 0)
                    continue;
                QuarterAt(state, start).Energy += energy * overlap / total;
            }
        }

        private static DefaPowerQuarter QuarterAt(DefaPowerConnectorState state, DateTimeOffset start)
        {
            var quarters = state.Quarters;
            for (var i = quarters.Count - 1; i >= 0; i--)
            {
                if (quarters[i].Start == start)
                    return quarters[i];
                if (quarters[i].Start < start)
                {
                    var added = new DefaPowerQuarter { Start = start };
                    quarters.Insert(i + 1, added);
                    return added;
                }
            }
            var oldest = new DefaPowerQuarter { Start = start };
            quarters.Insert(0, oldest);
            return oldest;
        }

        private static void Prune(DefaPowerConnectorState state, DateTimeOffset now)
        {
            var oldest = QuarterStart(now - Keep);
            state.Quarters.RemoveAll(x => x.Start < oldest);
        }

        /// <summary>The finished, complete quarters: a sample at or after their end, and none started before the first sample.</summary>
        public static List<DefaPowerQuarter> FinishedQuarters(DefaPowerConnectorState state)
        {
            if (state.SampleTime is not DateTimeOffset sample || state.Since is not DateTimeOffset since)
                return [];
            return state.Quarters.Where(x => x.Start >= since && x.Start + Quarter <= sample).ToList();
        }

        /// <summary>The finished hours whose four quarters are all known, with their energy.</summary>
        public static List<DefaPowerQuarter> FinishedHours(DefaPowerConnectorState state) =>
            FinishedQuarters(state)
                .GroupBy(x => HourStart(x.Start))
                .Where(g => g.Count() == 4)
                .Select(g => new DefaPowerQuarter { Start = g.Key, Energy = g.Sum(x => x.Energy) })
                .OrderBy(x => x.Start)
                .ToList();

        /// <summary>The energy since <paramref name="midnight"/>, the current quarter included.</summary>
        public static double EnergySince(DefaPowerConnectorState state, DateTimeOffset midnight) =>
            state.Quarters.Where(x => x.Start >= midnight).Sum(x => x.Energy);

        /// <summary>The price of the slot that holds the whole of [<paramref name="start"/>, <paramref name="end"/>).</summary>
        public static double? PriceOf(IReadOnlyList<DefaPowerPrice> prices, DateTimeOffset start, DateTimeOffset end) =>
            prices.LastOrDefault(x => x.Start <= start && end <= x.End)?.Price;

        /// <summary>The cost of a quarter in the cost unit, or null without a price.</summary>
        public static double? QuarterCost(DefaPowerQuarter quarter, IReadOnlyList<DefaPowerPrice> prices, string? priceUnit) =>
            PriceOf(prices, quarter.Start, quarter.Start + Quarter) is double price ? quarter.Energy * price * CostFactor(priceUnit) : null;

        /// <summary>
        /// The cost of the quarters from <paramref name="start"/>'s quarter until <paramref name="end"/>; null when a quarter
        /// with energy has no price.
        /// </summary>
        public static double? CostBetween(DefaPowerConnectorState state, DateTimeOffset start, DateTimeOffset end,
            IReadOnlyList<DefaPowerPrice> prices, string? priceUnit)
        {
            var first = QuarterStart(start);
            double cost = 0;
            foreach (var quarter in state.Quarters.Where(x => x.Start >= first && x.Start < end))
            {
                if (QuarterCost(quarter, prices, priceUnit) is double value)
                    cost += value;
                else if (quarter.Energy > 0)
                    return null;
            }
            return cost;
        }

        private static readonly string[] _subunits = ["c/", "ct", "cent", "snt", "øre", "öre"];

        /// <summary>
        /// What a price times kWh is multiplied by to get the cost. ElectricityPrice publishes cents per kWh (c/kWh, the
        /// default for a series without a unit), so the cost is in euros; a price per MWh is divided by 1000.
        /// </summary>
        public static double CostFactor(string? priceUnit)
        {
            var unit = (priceUnit ?? "").Trim().ToLowerInvariant();
            if (unit.Length == 0 || _subunits.Any(x => unit.StartsWith(x, StringComparison.Ordinal)))
                return 0.01;
            return unit.EndsWith("/mwh", StringComparison.Ordinal) ? 0.001 : 1;
        }

        /// <summary>The cost's unit for a price unit: kr for øre, € for cents and euros, otherwise the currency before the slash.</summary>
        public static string CostUnit(string? priceUnit)
        {
            var unit = (priceUnit ?? "").Trim();
            var lower = unit.ToLowerInvariant();
            if (lower.StartsWith("øre", StringComparison.Ordinal) || lower.StartsWith("öre", StringComparison.Ordinal))
                return "kr";
            if (CostFactor(unit) == 0.01)
                return "€";
            var slash = unit.IndexOf('/', StringComparison.Ordinal);
            var currency = slash > 0 ? unit[..slash].Trim() : "";
            return currency.Length == 0 || currency.Equals("eur", StringComparison.OrdinalIgnoreCase) ? "€" : currency;
        }

        /// <summary>
        /// Adds a price series (ElectricityPrice's <c>prices</c> report) to the known prices: its points replace the ones they
        /// overlap, and prices older than <see cref="KeepPrices"/> are dropped. Returns the number of points taken.
        /// </summary>
        public static int MergePrices(DefaPowerState state, ForecastSeries series, DateTimeOffset now)
        {
            var points = series.Points
                .Where(x => x.Value is double value && double.IsFinite(value) && x.End > x.Start)
                .Select(x => new DefaPowerPrice { Start = x.Start.ToUniversalTime(), End = x.End.ToUniversalTime(), Price = x.Value!.Value })
                .ToList();
            if (points.Count == 0)
                return 0;

            if (!string.IsNullOrWhiteSpace(series.Unit))
                state.PriceUnit = series.Unit.Trim();
            var first = points.Min(x => x.Start);
            var last = points.Max(x => x.End);
            var oldest = now - KeepPrices;
            state.Prices = state.Prices
                .Where(x => (x.End <= first || x.Start >= last) && x.End > oldest)
                .Concat(points.Where(x => x.End > oldest))
                .OrderBy(x => x.Start)
                .ToList();
            return points.Count;
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    }
}
