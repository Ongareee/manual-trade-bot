namespace QuantowerPropBot.Core;

/// <summary>A named trading session with start/end in ET (America/New_York) "HH:mm".</summary>
public sealed record SessionWindow(string Name, string Start, string End);

public enum TradeDirection { Both, Long, Short }

/// <summary>
/// Per-symbol strategy configuration — the exact analogue of the live YAML (mnq.yaml / gold.yaml).
/// Values are config-driven, never hardcoded in logic. Defaults below reflect the LIVE configs;
/// they should be loaded from a file in production.
/// </summary>
public sealed class SymbolConfig
{
    public required string Symbol { get; init; }
    public required double PointValue { get; init; }   // $ per 1.0 point
    public required double TickSize { get; init; }

    public int[] SmaPeriods { get; init; } = { 7, 30, 90, 180 };
    public double EntryBufferPoints { get; init; }

    public double LongTpPoints { get; init; }
    public double LongSlPoints { get; init; }   // hard SL cap (backstop)
    public double ShortTpPoints { get; init; }
    public double ShortSlPoints { get; init; }

    public int SlopeLookback { get; init; } = 36;
    public bool LongSlopeFilter { get; init; }
    public bool ShortSlopeFilter { get; init; }

    public bool UseTrendFilter { get; init; } = true;
    public double TrendScoreThreshold { get; init; } = 0.0;

    public TradeDirection Direction { get; init; } = TradeDirection.Both;

    public required IReadOnlyList<SessionWindow> Sessions { get; init; }

    public int MaxBars { get; init; } = 2500;  // ≥90d of H1 for the 3-month trend lookback

    // ---- Live values (source of truth: ~/trading_bot/configs/*.yaml) ----
    public static SymbolConfig Mnq() => new()
    {
        Symbol = "MNQ", PointValue = 2.00, TickSize = 0.25,
        EntryBufferPoints = 9.00,
        LongTpPoints = 60.00, LongSlPoints = 90.00,
        ShortTpPoints = 60.00, ShortSlPoints = 90.00,
        SlopeLookback = 36, LongSlopeFilter = true, ShortSlopeFilter = true,   // MNQ: both sides
        UseTrendFilter = true, TrendScoreThreshold = 0.0, Direction = TradeDirection.Both,
        Sessions = new[]
        {
            new SessionWindow("ASIA", "18:00", "03:00"),
            new SessionWindow("LONDON", "03:00", "11:00"),
            new SessionWindow("NEW_YORK", "08:30", "16:50"),
        },
    };

    public static SymbolConfig Mgc() => new()
    {
        Symbol = "MGC", PointValue = 10.00, TickSize = 0.10,
        EntryBufferPoints = 4.50,
        LongTpPoints = 22.00, LongSlPoints = 32.50,
        ShortTpPoints = 20.00, ShortSlPoints = 32.50,
        SlopeLookback = 36, LongSlopeFilter = false, ShortSlopeFilter = true,  // MGC: shorts only
        UseTrendFilter = true, TrendScoreThreshold = 0.0, Direction = TradeDirection.Both,
        Sessions = new[]
        {
            new SessionWindow("NY", "08:30", "16:50"),
            new SessionWindow("ASIA", "18:00", "03:00"),
        },
    };
}
