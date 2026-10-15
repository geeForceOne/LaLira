namespace LaLira.Core.Ledger;

// Round values for a chart's grid lines: steps of 1, 2, 2.5 or 5 times a power of ten,
// at least one whole currency unit, so the labels never need decimals.
public static class ChartScale
{
    public static IReadOnlyList<long> Ticks(double low, double high, int most = 4)
    {
        if (!(high > low)) return [];
        var target = (high - low) / 3;
        var power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(target, 100))));
        foreach (var power10 in new[] { power, power * 10, power * 100 })
            foreach (var factor in new[] { 1, 2, 2.5, 5 })
            {
                var step = Math.Max(100, (long)Math.Round(factor * power10));
                if (step < target * 0.75) continue;
                var first = (long)Math.Ceiling(low / step) * step;
                var ticks = new List<long>();
                for (var v = first; v <= high; v += step)
                    ticks.Add(v);
                if (ticks.Count <= most)
                    return ticks;
            }
        return [];
    }
}
