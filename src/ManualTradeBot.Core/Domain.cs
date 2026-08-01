using System.Text.Json;

namespace ManualTradeBot.Core;

public enum Direction { Long, Short }
public enum StrategyKind { SMA, ORB }
public enum ManualTradeStatus { PendingFill, Active, Closed, Skipped, Expired }

public sealed record TradeInstruction(
    string Asset, StrategyKind Strategy, Direction Direction, double Entry,
    double TakeProfit, double StopLoss, DateTime SignalUtc, string Detail);

public sealed record ManualTrade(
    string Id, TradeInstruction Instruction, ManualTradeStatus Status,
    double? FillPrice = null, double? ExitPrice = null, string? ExitReason = null,
    DateTime? ClosedUtc = null);

public interface IAlertSink
{
    void SendPreparation(string message);
    void SendEntry(string message);
    void SendStatus(string message);
}

/// <summary>Persists every manual state transition. No broker state is read or inferred.</summary>
public sealed class ManualTradeLedger
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly Dictionary<string, ManualTrade> _trades;

    public ManualTradeLedger(string path)
    {
        _path = path;
        _trades = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, ManualTrade>>(File.ReadAllText(path)) ?? new()
            : new();
    }

    public IReadOnlyCollection<ManualTrade> All { get { lock (_sync) return _trades.Values.ToArray(); } }

    public ManualTrade Create(TradeInstruction instruction)
    {
        lock (_sync)
        {
            var trade = new ManualTrade(Guid.NewGuid().ToString("N"), instruction, ManualTradeStatus.PendingFill);
            _trades[trade.Id] = trade; Save(); return trade;
        }
    }

    public ManualTrade? FindPending(StrategyKind strategy, string asset) => Find(strategy, asset, ManualTradeStatus.PendingFill);
    public ManualTrade? FindActive(StrategyKind strategy, string asset) => Find(strategy, asset, ManualTradeStatus.Active);

    public ManualTrade? Fill(StrategyKind strategy, string asset, double price)
        => Update(FindPending(strategy, asset), t => t with { Status = ManualTradeStatus.Active, FillPrice = price });
    public ManualTrade? Skip(StrategyKind strategy, string asset)
        => Update(FindPending(strategy, asset), t => t with { Status = ManualTradeStatus.Skipped, ClosedUtc = DateTime.UtcNow });
    public ManualTrade? Exit(StrategyKind strategy, string asset, double price, string reason)
        => Update(FindActive(strategy, asset), t => t with { Status = ManualTradeStatus.Closed, ExitPrice = price, ExitReason = reason, ClosedUtc = DateTime.UtcNow });

    public void ExpireBeyondEntry(string asset, StrategyKind strategy, double livePrice, double distancePoints)
    {
        var pending = FindPending(strategy, asset);
        if (pending is null) return;
        var i = pending.Instruction;
        bool beyond = i.Direction == Direction.Long ? livePrice >= i.Entry + distancePoints : livePrice <= i.Entry - distancePoints;
        if (beyond) Update(pending, t => t with { Status = ManualTradeStatus.Expired, ClosedUtc = DateTime.UtcNow });
    }

    private ManualTrade? Find(StrategyKind strategy, string asset, ManualTradeStatus status)
    {
        lock (_sync) return _trades.Values.LastOrDefault(t => t.Status == status && t.Instruction.Strategy == strategy && t.Instruction.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase));
    }
    private ManualTrade? Update(ManualTrade? trade, Func<ManualTrade, ManualTrade> update)
    {
        if (trade is null) return null;
        lock (_sync) { var changed = update(trade); _trades[changed.Id] = changed; Save(); return changed; }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_trades, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Parses the intentionally short manual protocol: `4100.25 SMA`, `skip ORB`, `4110 TP SMA`.</summary>
public static class ReplyParser
{
    public static bool TryParse(string input, out StrategyKind strategy, out double? price, out string action)
    {
        strategy = default; price = null; action = "";
        var words = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 2 || !Enum.TryParse(words[^1], true, out strategy)) return false;
        if (words[0].Equals("skip", StringComparison.OrdinalIgnoreCase)) { action = "skip"; return true; }
        if (!double.TryParse(words[0], System.Globalization.CultureInfo.InvariantCulture, out var p)) return false;
        price = p;
        action = words.Length == 2 ? "fill" : string.Join(' ', words[1..^1]);
        return true;
    }
}
