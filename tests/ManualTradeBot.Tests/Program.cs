using ManualTradeBot.Core;
using QuantowerPropBot.Core;

static void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); }

var cfg = new SymbolConfig
{
    Symbol = "TEST", PointValue = 1, TickSize = .25, SmaPeriods = new[] { 2, 3 }, EntryBufferPoints = 1,
    LongTpPoints = 10, LongSlPoints = 5, ShortTpPoints = 10, ShortSlPoints = 5,
    UseTrendFilter = false, LongSlopeFilter = false, ShortSlopeFilter = false,
    Sessions = new[] { new SessionWindow("ALL", "00:00", "23:59") }, MaxBars = 100
};
var engine = new SignalEngine(cfg);
var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
engine.SeedHistory(Enumerable.Range(0, 5).Select(i => new Bar(start.AddHours(i), 100, 100, 100, 100, 1)));
var forming = new Bar(start.AddHours(5), 100, 101, 99, 101, 1);
var preview = engine.PreviewPotential(forming, 10);
Check(preview is { Side: Side.Long }, "SMA potential approaches exact dynamic stack threshold");
Check(engine.BarCount == 5, "SMA preview does not mutate closed history");
var exact = engine.OnBarClose(forming with { Close = 102.75, High = 102.75 });
Check(exact is { Side: Side.Long }, "SMA exact signal fires only on closed bar");

var orb = new OrbEngine(new OrbConfig { Symbol = "TEST", PointValue = 1, TickSize = .25, UseWidthGuard = false });
for (int m = 0; m < 15; m++) orb.OnMinuteBar(new(start.AddDays(1).AddHours(14).AddMinutes(30 + m), 100, 110, 90, 100, 1));
orb.OnMinuteBar(new(start.AddDays(1).AddHours(14).AddMinutes(45), 111, 111, 111, 111, 1));
orb.OnMinuteBar(new(start.AddDays(1).AddHours(14).AddMinutes(46), 100, 100, 100, 100, 1));
Check(orb.ArmedApproach is { Side: Side.Long, EntryPrice: 110 }, "ORB exposes armed re-break approach");

Check(ReplyParser.TryParse("4100.25 SMA", out var strategy, out var price, out var action) && strategy == StrategyKind.SMA && price == 4100.25 && action == "fill", "fill reply protocol");
Check(ReplyParser.TryParse("4090 manual exit SMA", out _, out _, out action) && action == "manual exit", "exit reply protocol");
