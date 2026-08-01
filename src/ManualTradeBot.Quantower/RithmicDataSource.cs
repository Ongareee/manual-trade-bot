using System.Collections.Concurrent;
using QuantowerPropBot.Core;
using TradingPlatform.BusinessLayer;
using QtCore = TradingPlatform.BusinessLayer.Core;

namespace ManualTradeBot.Quantower;

/// <summary>Read-only Quantower adapter. This type exposes symbols and bars only.</summary>
internal sealed class RithmicDataSource
{
    private readonly QtCore _core = QtCore.Instance;
    private readonly Action<string> _log;
    private readonly string _connectionFilter;
    private readonly ConcurrentDictionary<string, Symbol> _symbols = new();

    public RithmicDataSource(string connectionFilter, Action<string> log)
    {
        _connectionFilter = connectionFilter;
        _log = log;
    }

    public Symbol Symbol(string root) => _symbols.GetOrAdd(root, Resolve);

    public IReadOnlyList<Bar> History(string root, Period period, DateTime from, DateTime to)
    {
        using var hd = Symbol(root).GetHistory(period, from, to);
        return Bars(hd).OrderBy(x => x.TimeUtc).ToArray();
    }

    public static IEnumerable<Bar> Bars(HistoricalData hd)
    {
        for (int i = 0; i < hd.Count; i++)
            if (hd[i] is HistoryItemBar b)
                yield return new Bar(DateTime.SpecifyKind(b.TimeLeft, DateTimeKind.Utc), b.Open, b.High, b.Low, b.Close, b.Volume);
    }

    private Symbol Resolve(string root)
    {
        // Resolve from loaded market-data symbols, never from an account or position collection.
        string connectionId = (_core.Symbols ?? Array.Empty<Symbol>())
            .Select(s => s.ConnectionId).FirstOrDefault(id => id?.Contains(_connectionFilter, StringComparison.OrdinalIgnoreCase) ?? false)
            ?? throw new InvalidOperationException($"No loaded symbol identifies the '{_connectionFilter}' data connection.");
        var candidates = Match(_core.Symbols, connectionId, root);
        if (candidates.Count == 0)
        {
            var found = _core.SearchSymbols(new SearchSymbolsRequestParameters
            {
                ConnectionId = connectionId, FilterName = root,
                SymbolTypes = new List<SymbolType> { SymbolType.Futures }
            });
            candidates = Match(found, connectionId, root);
        }
        var result = candidates.FirstOrDefault(x => x.Name.Equals(root, StringComparison.OrdinalIgnoreCase))
                     ?? candidates.OrderBy(x => x.Name.Length).FirstOrDefault()
                     ?? throw new InvalidOperationException($"No {root} futures symbol found on {connectionId}.");
        _log($"{root}: read-only feed resolved to {result.Name} on {connectionId}");
        return result;
    }

    private static List<Symbol> Match(IEnumerable<Symbol>? source, string connectionId, string root) =>
        (source ?? Array.Empty<Symbol>()).Where(s => s.ConnectionId == connectionId &&
            (s.Name.Equals(root, StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase))).ToList();
}
