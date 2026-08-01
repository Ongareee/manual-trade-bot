namespace QuantowerPropBot.Core;

/// <summary>A 2nd-breakout ORB entry signal (direction + reference price + ORB edges). The runner
/// anchors SL to the opposite edge and TP to <c>rr × risk</c> off the ACTUAL fill, not this price.</summary>
public readonly record struct OrbSignal(Side Side, double SignalPrice, double OrbHigh, double OrbLow);

/// <summary>
/// Streaming Opening-Range-Breakout engine — the live-adapted port of the ORB repos' `orb.py`
/// state machine + `session.py` windows + the ORB-width guard. Pure logic, broker-agnostic, fed one
/// CLOSED 1-minute bar at a time (Bar.TimeUtc = bar OPEN edge, per the Bar convention).
///
/// Per NY day:
///   1. Accumulate the ORB (high/low) over [09:30, 09:45) ET.
///   2. At 09:45 finalize it; if UseWidthGuard and today's width > P-th percentile of the trailing N
///      sessions' widths, SKIP the day (no trades).
///   3. From 09:45 to the entry cutoff (open + 3h → 12:30) run break→retest→re-break on 1-min BODY
///      closes; the 2nd breakout in the armed direction is the entry (one trade/day).
/// EOD-flat and the actual order/bracket live in the runner.
/// Matches `orb.py` exactly (incremental form of `detect_entry`); SL = opposite ORB edge.
/// </summary>
public sealed class OrbEngine
{
    private static readonly TimeZoneInfo Et = ResolveEastern();

    private enum Stage { NeedFirstBreak, NeedRetest, NeedRebreak }

    private readonly OrbConfig _cfg;
    private readonly TimeSpan _orbOpen, _orbEnd, _entryCutoff, _eodFlat;
    private readonly List<double> _widthHistory = new();   // trailing prior-session ORB widths (≤ N)

    // per-day state
    private DateTime _day = DateTime.MinValue;
    private bool _orbActive, _orbFinalized, _tradedToday, _skippedToday;
    private double _orbHigh, _orbLow;
    private Stage _stage = Stage.NeedFirstBreak;
    private Side? _armed;

    public OrbEngine(OrbConfig cfg)
    {
        _cfg = cfg;
        _orbOpen = ParseHhmm(cfg.OrbOpenEt);
        _orbEnd = _orbOpen + TimeSpan.FromMinutes(cfg.OrbMinutes);
        _entryCutoff = _orbOpen + TimeSpan.FromMinutes(cfg.EntryWindowMinutes);
        _eodFlat = ParseHhmm(cfg.EodFlatEt);
    }

    public string Symbol => _cfg.Symbol;
    public TimeSpan OrbOpenEt => _orbOpen;
    public TimeSpan EodFlatEt => _eodFlat;
    public bool TradedToday => _tradedToday;
    public bool SkippedToday => _skippedToday;
    public double? OrbHigh => _orbFinalized && !_skippedToday ? _orbHigh : null;
    public double? OrbLow => _orbFinalized && !_skippedToday ? _orbLow : null;

    /// <summary>Prime the width-guard reference with prior-session ORB widths (oldest→newest), computed
    /// from historical 1-min data at startup so day 1 has a real guard reference.</summary>
    public void SeedWidths(IEnumerable<double> priorWidths)
    {
        foreach (var w in priorWidths) PushWidth(w);
    }

    /// <summary>Feed one CLOSED 1-min bar. Returns an entry signal on the 2nd breakout, else null.
    /// Idempotent per bar is the caller's concern (dedupe on timestamp upstream).</summary>
    public OrbSignal? OnMinuteBar(in Bar bar)
    {
        DateTime et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(bar.TimeUtc, DateTimeKind.Utc), Et);
        DateTime day = et.Date;
        TimeSpan tod = et.TimeOfDay;

        if (day != _day) ResetDay(day);

        // 1. ORB accumulation window [orbOpen, orbEnd)
        if (tod >= _orbOpen && tod < _orbEnd)
        {
            if (!_orbActive) { _orbActive = true; _orbHigh = bar.High; _orbLow = bar.Low; }
            else { _orbHigh = Math.Max(_orbHigh, bar.High); _orbLow = Math.Min(_orbLow, bar.Low); }
            return null;
        }

        // 2. Finalize the ORB once, at/after orbEnd
        if (!_orbFinalized && tod >= _orbEnd)
        {
            if (_orbActive && _orbHigh > _orbLow)
            {
                _orbFinalized = true;
                double width = _orbHigh - _orbLow;
                _skippedToday = _cfg.UseWidthGuard && ShouldSkip(width);
                PushWidth(width);   // becomes a prior session for future days
            }
            else
            {
                // No valid ORB (bot started after the open, or degenerate range) — no trade today.
                _orbFinalized = true;
                _skippedToday = true;
            }
        }

        // 3. Entry-monitoring window [orbEnd, entryCutoff] — one trade/day.
        // NOTE: we do NOT mark "traded" here — the RUNNER calls MarkEntered() only after a fill.
        // So a signal that gets slot-blocked (MNQ contended by SMA) does not burn the day; while the
        // breakout structure persists the engine keeps offering the entry and ORB takes the first one
        // it can once the slot frees.
        if (_orbFinalized && !_skippedToday && !_tradedToday && tod >= _orbEnd && tod <= _entryCutoff)
            return Step(bar.Close);
        return null;
    }

    /// <summary>Runner calls this after a fill is actually taken — enforces one trade/day.</summary>
    public void MarkEntered() => _tradedToday = true;

    /// <summary>Incremental break→retest→re-break step on one 1-min body close. Mirrors orb.py.</summary>
    private OrbSignal? Step(double close)
    {
        int side = Classify(close);   // +1 above ORB / -1 below / 0 inside

        if (_stage == Stage.NeedFirstBreak)
        {
            if (side == 1) { _armed = Side.Long; _stage = Stage.NeedRetest; }
            else if (side == -1) { _armed = Side.Short; _stage = Stage.NeedRetest; }
            return null;
        }

        int armedSide = _armed == Side.Long ? 1 : -1;

        // Any opposite-side close before entry flips direction and requires a FRESH retest.
        if (side == -armedSide)
        {
            _armed = _armed == Side.Long ? Side.Short : Side.Long;
            _stage = Stage.NeedRetest;
            return null;
        }

        if (_stage == Stage.NeedRetest)
        {
            if (side == 0) _stage = Stage.NeedRebreak;   // body close back inside = retest
            return null;
        }

        // NeedRebreak
        if (side == armedSide)
            return new OrbSignal(_armed!.Value, close, _orbHigh, _orbLow);
        return null;
    }

    private int Classify(double close)
        => close > _orbHigh ? 1 : close < _orbLow ? -1 : 0;

    private void ResetDay(DateTime day)
    {
        _day = day;
        _orbActive = false; _orbFinalized = false; _tradedToday = false; _skippedToday = false;
        _orbHigh = 0; _orbLow = 0;
        _stage = Stage.NeedFirstBreak; _armed = null;
    }

    // ── ORB width guard ──
    private bool ShouldSkip(double width)
    {
        // Backtest parity: the guard is inactive until there are at least N prior sessions
        // (`len(prior_widths) >= guard_n`). _widthHistory is trimmed to N, so Count==N means
        // it holds exactly the trailing N (== `prior_widths[-guard_n:]`).
        if (_widthHistory.Count < _cfg.WidthLookback) return false;
        double threshold = Percentile(_widthHistory, _cfg.WidthPercentile);
        return width > threshold;
    }

    private void PushWidth(double width)
    {
        _widthHistory.Add(width);
        int keep = Math.Max(1, _cfg.WidthLookback);
        if (_widthHistory.Count > keep) _widthHistory.RemoveRange(0, _widthHistory.Count - keep);
    }

    /// <summary>numpy.percentile default (linear interpolation) over the trailing widths.
    /// VERIFY vs the ORB backtest's guard method before go-live (parity).</summary>
    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        var s = values.OrderBy(x => x).ToArray();
        int n = s.Length;
        if (n == 1) return s[0];
        double rank = (p / 100.0) * (n - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return s[lo];
        double frac = rank - lo;
        return s[lo] + frac * (s[hi] - s[lo]);
    }

    private static TimeSpan ParseHhmm(string hhmm)
    {
        var p = hhmm.Split(':');
        return new TimeSpan(int.Parse(p[0]), int.Parse(p[1]), 0);
    }

    private static TimeZoneInfo ResolveEastern()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
}
