namespace ManualTradeBot.Core;

public sealed record SmaConfig(string Asset, double Buffer, double LongTp, double LongSl, double ShortTp, double ShortSl, double ApproachPoints = 10);

/// <summary>Closed-H1 SMA model. Preview does not mutate history, so the five-minute alert cannot repaint the executed close.</summary>
public sealed class SmaSignalModel
{
    private readonly SmaConfig _cfg;
    private readonly List<(DateTime TimeUtc, double Close)> _bars = new();
    public SmaSignalModel(SmaConfig config) => _cfg = config;
    public void Seed(IEnumerable<(DateTime TimeUtc, double Close)> bars) { _bars.Clear(); _bars.AddRange(bars.OrderBy(x => x.TimeUtc)); }
    public void Close(DateTime timeUtc, double close) { if (_bars.Count == 0 || timeUtc > _bars[^1].TimeUtc) _bars.Add((timeUtc, close)); }

    public TradeInstruction? PreviewPotential(DateTime barTimeUtc, double livePrice)
    {
        var levels = Levels(barTimeUtc, livePrice); if (levels is null) return null;
        var (min, max) = levels.Value;
        // A long approaches the upper SMA+buffer from below; a short approaches the lower level from above.
        if (livePrice >= max + _cfg.Buffer - _cfg.ApproachPoints && livePrice < max + _cfg.Buffer)
            return Build(Direction.Long, max + _cfg.Buffer, barTimeUtc, "H1 closes in five minutes if price holds above the SMA stack");
        if (livePrice <= min - _cfg.Buffer + _cfg.ApproachPoints && livePrice > min - _cfg.Buffer)
            return Build(Direction.Short, min - _cfg.Buffer, barTimeUtc, "H1 closes in five minutes if price holds below the SMA stack");
        return null;
    }

    public TradeInstruction? OnClosedBar(DateTime barTimeUtc, double close)
    {
        var prior = _bars.Count == 0 ? null : Levels(_bars[^1].TimeUtc, _bars[^1].Close);
        var now = Levels(barTimeUtc, close); Close(barTimeUtc, close);
        if (prior is null || now is null) return null;
        var (oldMin, oldMax) = prior.Value; var (min, max) = now.Value;
        if (close > max + _cfg.Buffer && _bars.Count > 1 && _bars[^2].Close <= oldMax + _cfg.Buffer)
            return Build(Direction.Long, close, barTimeUtc, "H1 close crossed above SMA stack + buffer");
        if (close < min - _cfg.Buffer && _bars.Count > 1 && _bars[^2].Close >= oldMin - _cfg.Buffer)
            return Build(Direction.Short, close, barTimeUtc, "H1 close crossed below SMA stack - buffer");
        return null;
    }

    private (double Min, double Max)? Levels(DateTime pendingTimeUtc, double pendingClose)
    {
        var values = _bars.Select(x => x.Close).Append(pendingClose).ToArray();
        if (values.Length < 180) return null;
        var smas = new[] { 7, 30, 90, 180 }.Select(n => values[^n..].Average()).ToArray();
        return (smas.Min(), smas.Max());
    }
    private TradeInstruction Build(Direction direction, double entry, DateTime utc, string detail)
    {
        var tp = direction == Direction.Long ? entry + _cfg.LongTp : entry - _cfg.ShortTp;
        var sl = direction == Direction.Long ? entry - _cfg.LongSl : entry + _cfg.ShortSl;
        return new TradeInstruction(_cfg.Asset, StrategyKind.SMA, direction, entry, tp, sl, utc, detail);
    }
}

/// <summary>ORB alert gate. The host provides the existing validated ORB state: all prerequisites must already be true.</summary>
public static class OrbAlertGate
{
    public static TradeInstruction? Potential(string asset, Direction direction, double entry, double orbHigh, double orbLow, double rr, DateTime utc, double livePrice, double approach = 10)
    {
        bool approaching = direction == Direction.Long
            ? livePrice >= entry - approach && livePrice < entry
            : livePrice <= entry + approach && livePrice > entry;
        if (!approaching) return null;
        double risk = direction == Direction.Long ? entry - orbLow : orbHigh - entry;
        return Build(asset, direction, entry, orbHigh, orbLow, rr, utc, "ORB re-break armed; price approaching entry");
    }
    public static TradeInstruction Entry(string asset, Direction direction, double entry, double orbHigh, double orbLow, double rr, DateTime utc)
        => Build(asset, direction, entry, orbHigh, orbLow, rr, utc, "ORB final entry price reached");
    private static TradeInstruction Build(string asset, Direction direction, double entry, double high, double low, double rr, DateTime utc, string detail)
    {
        var risk = direction == Direction.Long ? entry - low : high - entry;
        var tp = direction == Direction.Long ? entry + risk * rr : entry - risk * rr;
        var sl = direction == Direction.Long ? low : high;
        return new TradeInstruction(asset, StrategyKind.ORB, direction, entry, tp, sl, utc, detail);
    }
}
