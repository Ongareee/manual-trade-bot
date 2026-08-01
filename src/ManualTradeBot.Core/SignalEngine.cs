namespace QuantowerPropBot.Core;

/// <summary>
/// Faithful C# port of the validated Python `StrategyEngine` (trading_bot/bot/strategy_engine.py).
/// Pure logic, no broker/platform dependency — this is the parity-critical core (PORT_SPEC §3-4, §7-8).
///
/// PARITY RULES (do not "improve"):
///  - SMAs computed on CLOSED bars only; NaN until enough history (matches pandas rolling().mean()).
///  - Signal fires on the FIRST cross only (the `and not prev` clause).
///  - Trend filter uses CALENDAR-time lookbacks (days), resolved by timestamp, not bar counts.
///  - sma_stack_exit uses the LAST CLOSED bar's SMAs (frozen), compared to the live price.
///  - Every numeric comparison mirrors the Python exactly, including the NaN / insufficient-history
///    branches that BLOCK filtered signals.
/// </summary>
public sealed class SignalEngine
{
    private static readonly TimeZoneInfo Et = ResolveEastern();

    private readonly SymbolConfig _cfg;
    private readonly List<Bar> _bars = new();
    private readonly int _maxPeriod;

    private double[]? _lastSmas;   // SMA of each period at the last closed bar (frozen); used by SmaStackExit
    private double _trendScore;

    public SignalEngine(SymbolConfig cfg)
    {
        _cfg = cfg;
        _maxPeriod = _cfg.SmaPeriods.Max();
    }

    public int BarCount => _bars.Count;

    // Read-only diagnostics (for per-bar signal logging, PORT_SPEC §13). No logic dependency.
    public IReadOnlyList<double>? LastSmas => _lastSmas;
    public double TrendScore => _trendScore;
    public double LastClose => _bars.Count > 0 ? _bars[^1].Close : double.NaN;

    public void SeedHistory(IEnumerable<Bar> bars)
    {
        _bars.Clear();
        _bars.AddRange(bars.OrderBy(b => b.TimeUtc));
        TrimAndRecalculate();
    }

    /// <summary>Append a just-closed H1 bar and return a Signal if one fires. Mirrors on_bar_close().</summary>
    public Signal? OnBarClose(in Bar bar)
    {
        _bars.Add(bar);
        TrimAndRecalculate();
        return CheckSignal();
    }

    /// <summary>
    /// Evaluate the still-forming H1 bar without changing engine state. Used exactly once at T-5m.
    /// A candidate is returned only when price is approaching the relevant stack+buffer from the
    /// opposite side and every non-price rule (first-cross, direction, slope and trend) would pass.
    /// </summary>
    public Signal? PreviewPotential(in Bar formingBar, double approachPoints)
    {
        if (_bars.Count < _maxPeriod || approachPoints <= 0) return null;

        _bars.Add(formingBar);
        Recalculate();
        try
        {
            // Solve the close-vs-SMA inequality exactly because the forming close is itself part
            // of every SMA: C > (sum(previous p-1 closes)+C)/p + buffer.
            var longLevels = new List<double>(); var shortLevels = new List<double>();
            foreach (int p in _cfg.SmaPeriods)
            {
                if (p <= 1 || _bars.Count - 1 < p - 1) return null;
                double priorSum = 0;
                for (int j = _bars.Count - p; j < _bars.Count - 1; j++) priorSum += _bars[j].Close;
                longLevels.Add((priorSum + p * _cfg.EntryBufferPoints) / (p - 1));
                shortLevels.Add((priorSum - p * _cfg.EntryBufferPoints) / (p - 1));
            }
            double longEntry = longLevels.Max();
            double shortEntry = shortLevels.Min();
            double px = formingBar.Close;

            if (px >= longEntry - approachPoints && px < longEntry &&
                PreviewAtPrice(formingBar, longEntry + _cfg.TickSize) is Signal l && l.Side == Side.Long)
                return l with { EntryPrice = longEntry };

            if (px <= shortEntry + approachPoints && px > shortEntry &&
                PreviewAtPrice(formingBar, shortEntry - _cfg.TickSize) is Signal s && s.Side == Side.Short)
                return s with { EntryPrice = shortEntry };

            return null;
        }
        finally
        {
            _bars.RemoveAt(_bars.Count - 1);
            Recalculate();
        }
    }

    private Signal? PreviewAtPrice(in Bar formingBar, double close)
    {
        var original = _bars[^1];
        _bars[^1] = formingBar with { Close = close, High = Math.Max(formingBar.High, close), Low = Math.Min(formingBar.Low, close) };
        Recalculate();
        try { return CheckSignal(); }
        finally { _bars[^1] = original; Recalculate(); }
    }

    private void TrimAndRecalculate()
    {
        if (_bars.Count > _cfg.MaxBars)
            _bars.RemoveRange(0, _bars.Count - _cfg.MaxBars);
        Recalculate();
    }

    private void Recalculate()
    {
        int last = _bars.Count - 1;
        if (last < 0) { _lastSmas = null; _trendScore = 0.0; return; }

        _lastSmas = new double[_cfg.SmaPeriods.Length];
        for (int k = 0; k < _cfg.SmaPeriods.Length; k++)
            _lastSmas[k] = SmaAt(_cfg.SmaPeriods[k], last);

        _trendScore = _cfg.UseTrendFilter ? ComputeTrendScore() : 0.0;
    }

    /// <summary>Rolling mean of Close over `period` bars ending at `index`; NaN if insufficient. == pandas rolling(period).mean().</summary>
    private double SmaAt(int period, int index)
    {
        if (index < 0 || index >= _bars.Count || index < period - 1)
            return double.NaN;
        double sum = 0.0;
        for (int j = index - period + 1; j <= index; j++)
            sum += _bars[j].Close;
        return sum / period;
    }

    /// <summary>Port of _compute_trend_score: weighted sign of calendar-period momentum.</summary>
    private double ComputeTrendScore()
    {
        if (_bars.Count < 2) return 0.0;
        (int days, double weight)[] periods = { (90, 0.10), (30, 0.30), (14, 0.30), (1, 0.30) };

        int last = _bars.Count - 1;
        double currentClose = _bars[last].Close;
        DateTime currentTs = _bars[last].TimeUtc;

        double score = 0.0;
        foreach (var (days, weight) in periods)
        {
            DateTime refTs = currentTs - TimeSpan.FromDays(days);
            int idx = LastIndexAtOrBefore(refTs);            // == np.searchsorted(times, ref, 'right') - 1
            if (idx >= 0 && _bars[idx].Close != 0.0)
                score += weight * Math.Sign(currentClose - _bars[idx].Close);
        }
        return score;
    }

    /// <summary>Largest index i with _bars[i].TimeUtc &lt;= ts, or -1. (bars are time-ascending)</summary>
    private int LastIndexAtOrBefore(DateTime ts)
    {
        int lo = 0, hi = _bars.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_bars[mid].TimeUtc <= ts) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Port of _check_signal. Returns a Signal or null. Uses only closed-bar data (no repaint).</summary>
    private Signal? CheckSignal()
    {
        if (_bars.Count < 2) return null;
        int i = _bars.Count - 1, iPrev = i - 1;

        // SMAs at current and previous bar for every period
        var smaNow = new double[_cfg.SmaPeriods.Length];
        var smaPrev = new double[_cfg.SmaPeriods.Length];
        for (int k = 0; k < _cfg.SmaPeriods.Length; k++)
        {
            smaNow[k] = SmaAt(_cfg.SmaPeriods[k], i);
            smaPrev[k] = SmaAt(_cfg.SmaPeriods[k], iPrev);
        }
        if (smaNow.Any(double.IsNaN) || smaPrev.Any(double.IsNaN)) return null;

        double closeNow = _bars[i].Close, closePrev = _bars[iPrev].Close;
        double smaMax = smaNow.Max(), smaMin = smaNow.Min();
        double prevMax = smaPrev.Max(), prevMin = smaPrev.Min();
        double buf = _cfg.EntryBufferPoints;

        bool longSig = (closeNow > smaMax + buf) && !(closePrev > prevMax + buf);
        bool shortSig = (closeNow < smaMin - buf) && !(closePrev < prevMin - buf);

        if (_cfg.Direction == TradeDirection.Short) longSig = false;
        else if (_cfg.Direction == TradeDirection.Long) shortSig = false;

        // --- Slope filter on the longest SMA (mirrors the exact branches, incl. block-on-insufficient-history) ---
        if (i >= _cfg.SlopeLookback)
        {
            double sma180Now = SmaAt(_maxPeriod, i);
            double sma180Past = SmaAt(_maxPeriod, i - _cfg.SlopeLookback);
            if (double.IsNaN(sma180Past))
            {
                if (_cfg.LongSlopeFilter) longSig = false;
                if (_cfg.ShortSlopeFilter) shortSig = false;
            }
            else
            {
                if (longSig && _cfg.LongSlopeFilter && sma180Now <= sma180Past) longSig = false;
                if (shortSig && _cfg.ShortSlopeFilter && sma180Now >= sma180Past) shortSig = false;
            }
        }
        else
        {
            if (_cfg.LongSlopeFilter) longSig = false;
            if (_cfg.ShortSlopeFilter) shortSig = false;
        }

        // --- Trend filter ---
        if (_cfg.UseTrendFilter)
        {
            double score = _trendScore, thresh = _cfg.TrendScoreThreshold;
            if (score > thresh) shortSig = false;
            else if (score < -thresh) longSig = false;
            else { longSig = false; shortSig = false; }
        }

        if (longSig && shortSig) return null;           // ambiguous → no trade (matches Python)
        if (longSig) return BuildSignal(Side.Long, closeNow);
        if (shortSig) return BuildSignal(Side.Short, closeNow);
        return null;
    }

    private Signal BuildSignal(Side side, double entryPrice)
    {
        DateTime barEt = TimeZoneInfo.ConvertTimeFromUtc(_bars[^1].TimeUtc, Et);
        int barMins = barEt.Hour * 60 + barEt.Minute;
        string session = "UNKNOWN";
        foreach (var s in _cfg.Sessions)
            if (InWindow(barMins, ToMins(s.Start), ToMins(s.End))) { session = s.Name; break; }

        double tp = side == Side.Long ? _cfg.LongTpPoints : _cfg.ShortTpPoints;
        double sl = side == Side.Long ? _cfg.LongSlPoints : _cfg.ShortSlPoints;
        return new Signal(side, entryPrice, tp, sl, _cfg.Symbol, session);
    }

    /// <summary>Port of sma_stack_exit: 2+ of the LAST CLOSED bar's SMAs on the wrong side of price.</summary>
    public bool SmaStackExit(double currentPrice, Side side)
    {
        if (_lastSmas is null || _lastSmas.Length == 0) return false;
        if (_lastSmas.Any(double.IsNaN)) return false;
        int wrong = 0;
        foreach (double v in _lastSmas)
        {
            if (side == Side.Long) { if (v > currentPrice) wrong++; }
            else { if (v < currentPrice) wrong++; }
        }
        return wrong >= 2;
    }

    /// <summary>THE session test (PORT_SPEC §8). now must be ET.</summary>
    public bool InSession(DateTime nowEt)
    {
        int mins = nowEt.Hour * 60 + nowEt.Minute;
        foreach (var s in _cfg.Sessions)
            if (InWindow(mins, ToMins(s.Start), ToMins(s.End))) return true;
        return false;
    }

    public bool InSessionUtc(DateTime nowUtc) => InSession(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, Et));

    // ---- helpers ----
    private static int ToMins(string hhmm)
    {
        var p = hhmm.Split(':');
        return int.Parse(p[0]) * 60 + int.Parse(p[1]);
    }

    /// <summary>Port of _in_window — handles overnight (start &gt; end) windows like ASIA 18:00→03:00.</summary>
    private static bool InWindow(int nowMins, int startMins, int endMins)
        => startMins <= endMins
            ? startMins <= nowMins && nowMins < endMins
            : nowMins >= startMins || nowMins < endMins;

    private static TimeZoneInfo ResolveEastern()
    {
        // .NET 6+ resolves IANA ids on Windows via ICU; fall back to the Windows id if needed.
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
    }
}
