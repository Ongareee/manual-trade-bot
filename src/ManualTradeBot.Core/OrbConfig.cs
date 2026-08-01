namespace QuantowerPropBot.Core;

/// <summary>
/// Per-symbol Opening-Range-Breakout configuration — the analogue of the ORB repos' `*_orb.yaml`.
/// Defaults below are the FINALIZED live params (mnq_orb #1, mes_orb Config A) as of 2026-07-26.
/// Values are config-driven; the static factories mirror the SMA `SymbolConfig` pattern.
/// </summary>
public sealed class OrbConfig
{
    public required string Symbol { get; init; }
    public required double PointValue { get; init; }   // $ per index point
    public required double TickSize { get; init; }

    // ── Opening range ──
    public string OrbOpenEt { get; init; } = "09:30";  // NY RTH index open (ET)
    public int OrbMinutes { get; init; } = 15;         // opening-range candle length

    // ── Entry / exits ──
    public int EntryWindowMinutes { get; init; } = 180;   // no new entries after open + 3h (→ 12:30)
    public string EodFlatEt { get; init; } = "15:55";     // force-flat, no overnight holds
    public double TakeProfitRr { get; init; } = 2.0;      // TP = rr × risk (risk = |entry − opposite edge|)
    // SL is always the opposite ORB edge (long → ORB low, short → ORB high).

    // ── ORB width guard (skip abnormally wide opening ranges; DD control) ──
    public bool UseWidthGuard { get; init; } = true;
    public int WidthLookback { get; init; } = 45;      // N: trailing sessions in the width reference
    public int WidthPercentile { get; init; } = 94;    // P: skip if today's width > this percentile

    // FINALIZED #1 — MNQ (Micro E-mini Nasdaq-100): rr2 / n45 / p94, guard ON.
    public static OrbConfig Mnq() => new()
    {
        Symbol = "MNQ", PointValue = 2.00, TickSize = 0.25,
        TakeProfitRr = 2.0,
        UseWidthGuard = true, WidthLookback = 45, WidthPercentile = 94,
    };

    // FINALIZED Config A — MES (Micro E-mini S&P 500): rr2 / n25 / p80, guard ON.
    public static OrbConfig Mes() => new()
    {
        Symbol = "MES", PointValue = 5.00, TickSize = 0.25,
        TakeProfitRr = 2.0,
        UseWidthGuard = true, WidthLookback = 25, WidthPercentile = 80,
    };
}
