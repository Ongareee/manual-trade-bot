using ManualTradeBot.Core;
using QuantowerPropBot.Core;
using TradingPlatform.BusinessLayer;
using SignalSide = QuantowerPropBot.Core.Side;

namespace ManualTradeBot.Quantower;

/// <summary>Read-only Rithmic signal host. Contains no account position, order, or execution methods.</summary>
public sealed class ManualTradeStrategy : Strategy
{
    [InputParameter("Rithmic connection contains", 10)] public string ConnectionFilter = "Rithmic";
    [InputParameter("Telegram config", 20)] public string TelegramConfig = @"C:\trading\manual-trade-bot\telegram.json";
    [InputParameter("State file", 30)] public string StateFile = @"C:\trading\manual-trade-bot\state.json";
    [InputParameter("Enable ORB", 40)] public bool EnableOrb = true;

    private sealed class SmaFeed(SymbolConfig config, SignalEngine engine, Symbol symbol, HistoricalData history)
    { public SymbolConfig Config = config; public SignalEngine Engine = engine; public Symbol Symbol = symbol; public HistoricalData History = history; public DateTime LastClosed; public double Last; }
    private sealed class OrbFeed(OrbConfig config, OrbEngine engine, Symbol symbol, HistoricalData history)
    { public OrbConfig Config = config; public OrbEngine Engine = engine; public Symbol Symbol = symbol; public HistoricalData History = history; public DateTime LastClosed; public double Last; }

    private readonly Dictionary<string, SmaFeed> _sma = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrbFeed> _orb = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Symbol> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _orbPrepared = new();
    private readonly object _gate = new();
    private RithmicDataSource? _data;
    private TelegramClient? _telegram;
    private SignalCoordinator? _signals;
    private System.Threading.Timer? _timer;
    private int _running;
    private static readonly TimeZoneInfo Et = ResolveEastern();

    public ManualTradeStrategy() { Name = "Manual Trade Bot (Read Only)"; }

    protected override void OnRun()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            TelegramClient? telegram = null;
            _signals = new SignalCoordinator(StateFile, m => telegram?.Prep(m), m => telegram?.Entry(m), m => telegram?.Status(m), Info);
            telegram = new TelegramClient(TelegramConfig, Info, _signals.Reply);
            _telegram = telegram;
            _signals.Filled += t => { if (t.Instruction.Strategy == StrategyKind.ORB && _orb.TryGetValue(t.Instruction.Asset, out var f)) f.Engine.MarkEntered(); };
            _data = new RithmicDataSource(ConnectionFilter, Info);

            AddSma(SymbolConfig.Mnq()); AddSma(SymbolConfig.Mgc());
            if (EnableOrb) { AddOrb(OrbConfig.Mes()); AddOrb(OrbConfig.Mnq()); }
            foreach (var symbol in _subscribed.Values) symbol.NewLast += OnLast;
            foreach (var f in _sma.Values) f.History.NewHistoryItem += OnHistory;
            foreach (var f in _orb.Values) f.History.NewHistoryItem += OnHistory;
            _telegram.Start();
            _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(StateFile)!, "heartbeat.txt"), DateTime.UtcNow.ToString("O"));
            Info("Manual Trade Bot running — read-only Rithmic data; SMA MNQ/MGC; ORB MES/MNQ");
            _telegram.Status("Manual Trade Bot online\nQuantower/Rithmic data only — no order capability.");
        }
        catch (Exception e) { Log($"Manual Trade Bot startup failed: {e}", StrategyLoggingLevel.Error); throw; }
    }

    private void AddSma(SymbolConfig cfg)
    {
        var sym = _data!.Symbol(cfg.Symbol);
        var seed = _data.History(cfg.Symbol, Period.HOUR1, DateTime.UtcNow.AddDays(-150), DateTime.UtcNow).TakeLast(cfg.MaxBars).ToArray();
        var engine = new SignalEngine(cfg); engine.SeedHistory(seed);
        var live = sym.GetHistory(new HistoryRequestParameters { Symbol = sym, FromTime = DateTime.UtcNow.AddDays(-10), Aggregation = new HistoryAggregationTime(Period.HOUR1, sym.HistoryType) });
        var f = new SmaFeed(cfg, engine, sym, live) { LastClosed = seed.LastOrDefault().TimeUtc };
        _sma[cfg.Symbol] = f; _subscribed[sym.Name] = sym;
        Info($"{cfg.Symbol} SMA seeded {seed.Length} H1 bars");
    }

    private void AddOrb(OrbConfig cfg)
    {
        var sym = _data!.Symbol(cfg.Symbol);
        var minutes = _data.History(cfg.Symbol, Period.MIN1, DateTime.UtcNow.AddDays(-65), DateTime.UtcNow).ToArray();
        var engine = new OrbEngine(cfg);
        DateTime todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Et).Date;
        engine.SeedWidths(Widths(minutes, cfg).Where(x => x.Day < todayEt).Select(x => x.Width));
        DateTime last = DateTime.MinValue;
        foreach (var b in minutes.Where(b => TimeZoneInfo.ConvertTimeFromUtc(b.TimeUtc, Et).Date == todayEt)) { engine.OnMinuteBar(b); last = b.TimeUtc; }
        var live = sym.GetHistory(new HistoryRequestParameters { Symbol = sym, FromTime = DateTime.UtcNow.AddDays(-2), Aggregation = new HistoryAggregationTime(Period.MIN1, sym.HistoryType) });
        _orb[cfg.Symbol] = new OrbFeed(cfg, engine, sym, live) { LastClosed = last };
        _subscribed[sym.Name] = sym;
        Info($"{cfg.Symbol} ORB initialized from {minutes.Length} minute bars");
    }

    private void OnLast(Symbol symbol, Last last)
    {
        lock (_gate)
        {
            foreach (var f in _sma.Values.Where(x => x.Symbol.Name == symbol.Name)) { f.Last = last.Price; _signals?.OnPrice(f.Config.Symbol, StrategyKind.SMA, last.Price); }
            foreach (var f in _orb.Values.Where(x => x.Symbol.Name == symbol.Name)) { f.Last = last.Price; _signals?.OnPrice(f.Config.Symbol, StrategyKind.ORB, last.Price); CheckOrbPrice(f, last.Price); }
        }
    }

    private void CheckOrbPrice(OrbFeed f, double price)
    {
        var a = f.Engine.ArmedApproach; if (a is null || !OrbWindow(DateTime.UtcNow)) return;
        string id = $"{EtDate(DateTime.UtcNow):yyyyMMdd}|{a.Value.Side}|{a.Value.EntryPrice:F4}";
        bool approach = a.Value.Side == SignalSide.Long ? price >= a.Value.EntryPrice - 10 && price < a.Value.EntryPrice : price <= a.Value.EntryPrice + 10 && price > a.Value.EntryPrice;
        if (approach)
        {
            _orbPrepared.Add($"{f.Config.Symbol}|{id}");
            _signals?.Preparation(SignalCoordinator.Orb(f.Config.Symbol, a.Value.Side, a.Value.EntryPrice, a.Value.OrbHigh, a.Value.OrbLow, f.Config.TakeProfitRr, DateTime.UtcNow, "All ORB conditions pass; final price approach is active."), id);
        }
        bool reached = a.Value.Side == SignalSide.Long ? price >= a.Value.EntryPrice : price <= a.Value.EntryPrice;
        if (reached && _orbPrepared.Contains($"{f.Config.Symbol}|{id}"))
            _signals?.Entry(SignalCoordinator.Orb(f.Config.Symbol, a.Value.Side, a.Value.EntryPrice, a.Value.OrbHigh, a.Value.OrbLow, f.Config.TakeProfitRr, DateTime.UtcNow, "ORB entry level reached from the qualifying side."), id);
    }

    private void OnHistory(object sender, HistoryEventArgs args) => Tick();
    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            lock (_gate)
            {
                foreach (var f in _sma.Values) { FeedSma(f); PreviewSma(f); }
                foreach (var f in _orb.Values) FeedOrb(f);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(StateFile)!, "heartbeat.txt"), DateTime.UtcNow.ToString("O"));
            }
        }
        catch (Exception e) { Info($"Runtime check failed: {e.Message}"); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private void FeedSma(SmaFeed f)
    {
        foreach (var b in Closed(f.History, f.LastClosed))
        {
            f.LastClosed = b.TimeUtc;
            var s = f.Engine.OnBarClose(b);
            if (s is not null && f.Engine.InSessionUtc(b.TimeUtc))
                _signals?.Entry(SignalCoordinator.Sma(s, b.TimeUtc, "All SMA close, first-cross, slope, trend and session rules passed."), b.TimeUtc.ToString("O"));
        }
    }

    private void PreviewSma(SmaFeed f)
    {
        if (f.Last == 0 || !TryForming(f.History, out var b, out var closeUtc)) return;
        var left = closeUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromMinutes(5) || !f.Engine.InSessionUtc(b.TimeUtc)) return;
        var forming = b with { Close = f.Last, High = Math.Max(b.High, f.Last), Low = Math.Min(b.Low, f.Last) };
        var s = f.Engine.PreviewPotential(forming, 10);
        if (s is not null) _signals?.Preparation(SignalCoordinator.Sma(s, DateTime.UtcNow, "Five minutes remain in H1; all non-final SMA rules currently pass."), b.TimeUtc.ToString("O"));
    }

    private void FeedOrb(OrbFeed f)
    {
        foreach (var b in Closed(f.History, f.LastClosed)) { f.LastClosed = b.TimeUtc; _ = f.Engine.OnMinuteBar(b); }
        if (f.Last != 0) CheckOrbPrice(f, f.Last);
    }

    private static IEnumerable<Bar> Closed(HistoricalData h, DateTime after)
    {
        var result = new List<Bar>();
        for (int i = 0; i < h.Count; i++) if (h[i] is HistoryItemBar x)
        {
            var open = DateTime.SpecifyKind(x.TimeLeft, DateTimeKind.Utc);
            var close = DateTime.SpecifyKind(x.TimeRight, DateTimeKind.Utc);
            if (open > after && close <= DateTime.UtcNow) result.Add(new(open, x.Open, x.High, x.Low, x.Close, x.Volume));
        }
        return result.OrderBy(b => b.TimeUtc);
    }

    private static bool TryForming(HistoricalData h, out Bar bar, out DateTime closeUtc)
    {
        bar = default; closeUtc = default;
        for (int i = 0; i < h.Count; i++) if (h[i] is HistoryItemBar x)
        {
            var close = DateTime.SpecifyKind(x.TimeRight, DateTimeKind.Utc);
            if (close > DateTime.UtcNow && (closeUtc == default || close < closeUtc)) { closeUtc = close; bar = new(DateTime.SpecifyKind(x.TimeLeft, DateTimeKind.Utc), x.Open, x.High, x.Low, x.Close, x.Volume); }
        }
        return closeUtc != default;
    }

    private static IEnumerable<(DateTime Day, double Width)> Widths(IEnumerable<Bar> bars, OrbConfig cfg)
    {
        var open = TimeSpan.Parse(cfg.OrbOpenEt); var end = open + TimeSpan.FromMinutes(cfg.OrbMinutes);
        return bars.Select(b => (Bar: b, Et: TimeZoneInfo.ConvertTimeFromUtc(b.TimeUtc, Et)))
            .Where(x => x.Et.TimeOfDay >= open && x.Et.TimeOfDay < end).GroupBy(x => x.Et.Date)
            .Select(g => (g.Key, g.Max(x => x.Bar.High) - g.Min(x => x.Bar.Low))).Where(x => x.Item2 > 0);
    }
    private static bool OrbWindow(DateTime utc) { var t = TimeZoneInfo.ConvertTimeFromUtc(utc, Et).TimeOfDay; return t >= new TimeSpan(9,45,0) && t <= new TimeSpan(12,30,0); }
    private static DateTime EtDate(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Et).Date;
    private void Info(string m) { Log(m, StrategyLoggingLevel.Info); try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(StateFile)!, "manual-trade-bot.log"), $"{DateTime.UtcNow:O} {m}{Environment.NewLine}"); } catch { } }

    protected override void OnStop()
    {
        _timer?.Dispose(); foreach (var s in _subscribed.Values) s.NewLast -= OnLast;
        foreach (var f in _sma.Values) { f.History.NewHistoryItem -= OnHistory; f.History.Dispose(); }
        foreach (var f in _orb.Values) { f.History.NewHistoryItem -= OnHistory; f.History.Dispose(); }
        _telegram?.Dispose(); _sma.Clear(); _orb.Clear(); _subscribed.Clear(); Info("Manual Trade Bot stopped");
    }
    private static TimeZoneInfo ResolveEastern() { try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } }
}
