using System.Text.Json;
using ManualTradeBot.Core;
using QSide = QuantowerPropBot.Core.Side;

namespace ManualTradeBot.Quantower;

internal sealed class SignalCoordinator
{
    private readonly ManualTradeLedger _ledger;
    private readonly string _dedupePath;
    private readonly HashSet<string> _sent;
    private readonly Action<string> _prep, _entry, _status, _log;
    public event Action<ManualTrade>? Filled;

    public SignalCoordinator(string statePath, Action<string> prep, Action<string> entry, Action<string> status, Action<string> log)
    {
        _ledger = new ManualTradeLedger(statePath);
        _dedupePath = Path.ChangeExtension(statePath, ".signals.json");
        _sent = File.Exists(_dedupePath) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_dedupePath)) ?? new() : new();
        _prep = prep; _entry = entry; _status = status; _log = log;
    }

    public bool Preparation(TradeInstruction i, string setupId)
    {
        if (!Once("PREP", i, setupId)) return false;
        _prep($"⚠️ POTENTIAL {i.Asset} {i.Direction.ToString().ToUpperInvariant()}\nStrategy: {i.Strategy}\nEntry area: {i.Entry:F2}\nCurrent setup is within 10 points\n{i.Detail}");
        return true;
    }

    public void Entry(TradeInstruction i, string setupId)
    {
        if (!Once("ENTRY", i, setupId)) return;
        _ledger.Create(i);
        _entry($"🚨 ENTER NOW — MARKET\nAsset: {i.Asset}\nDirection: {i.Direction.ToString().ToUpperInvariant()}\nReference: {i.Entry:F2}\nTP: {i.TakeProfit:F2}\nSL: {i.StopLoss:F2}\nStrategy: {i.Strategy}\nReply: fill-price {i.Strategy}  |  skip {i.Strategy}");
    }

    public void OnPrice(string asset, StrategyKind strategy, double price)
    {
        var before = _ledger.Pending(strategy).Where(t => t.Instruction.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)).Select(t => t.Id).ToHashSet();
        _ledger.ExpireBeyondEntry(asset, strategy, price, 10);
        var after = _ledger.Pending(strategy).Select(t => t.Id).ToHashSet();
        if (before.Except(after).Any()) _status($"EXPIRED — {asset} {strategy}\nPrice moved 10 points beyond entry without a reported fill.");
    }

    public Task Reply(string raw)
    {
        if (!ReplyParser.TryParse(raw, out var strategy, out var price, out var action))
        {
            _status("Reply not understood. Use: 4100.25 SMA | skip ORB | 4110.00 TP SMA"); return Task.CompletedTask;
        }
        if (action == "skip")
        {
            var p = _ledger.Pending(strategy).FirstOrDefault();
            if (p is null) _status($"No pending {strategy} instruction to skip.");
            else { _ledger.Skip(strategy, p.Instruction.Asset); _status($"SKIPPED — {p.Instruction.Asset} {strategy}"); }
            return Task.CompletedTask;
        }
        if (action == "fill" && price is double fill)
        {
            var p = _ledger.Pending(strategy).OrderBy(t => Math.Abs(t.Instruction.Entry - fill)).FirstOrDefault();
            if (p is null) _status($"No pending {strategy} instruction for that fill.");
            else
            {
                var done = _ledger.Fill(strategy, p.Instruction.Asset, fill)!;
                _status($"FILLED — {p.Instruction.Asset} {strategy} {p.Instruction.Direction} @ {fill:F2}\nTP {p.Instruction.TakeProfit:F2} | SL {p.Instruction.StopLoss:F2}\nReply on exit: exit-price reason {strategy}");
                Filled?.Invoke(done);
            }
            return Task.CompletedTask;
        }
        if (price is double exit)
        {
            var active = _ledger.Active(strategy).FirstOrDefault();
            if (active is null) _status($"No active manual {strategy} trade to close.");
            else { _ledger.Exit(strategy, active.Instruction.Asset, exit, action); _status($"CLOSED — {active.Instruction.Asset} {strategy} @ {exit:F2}\nReason: {action}"); }
        }
        return Task.CompletedTask;
    }

    private bool Once(string kind, TradeInstruction i, string setupId)
    {
        string key = $"{kind}|{i.Strategy}|{i.Asset}|{setupId}";
        lock (_sent)
        {
            if (!_sent.Add(key)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_dedupePath)!);
            File.WriteAllText(_dedupePath, JsonSerializer.Serialize(_sent));
            return true;
        }
    }

    public static TradeInstruction Sma(QuantowerPropBot.Core.Signal s, DateTime utc, string detail) =>
        new(s.Symbol, StrategyKind.SMA, s.Side == QSide.Long ? Direction.Long : Direction.Short,
            s.EntryPrice, s.Side == QSide.Long ? s.EntryPrice + s.TpPoints : s.EntryPrice - s.TpPoints,
            s.Side == QSide.Long ? s.EntryPrice - s.SlPoints : s.EntryPrice + s.SlPoints, utc, detail);
    public static TradeInstruction Orb(string asset, QSide side, double entry, double high, double low, double rr, DateTime utc, string detail)
    {
        double risk = side == QSide.Long ? entry - low : high - entry;
        return new(asset, StrategyKind.ORB, side == QSide.Long ? Direction.Long : Direction.Short, entry,
            side == QSide.Long ? entry + rr * risk : entry - rr * risk, side == QSide.Long ? low : high, utc, detail);
    }
}
