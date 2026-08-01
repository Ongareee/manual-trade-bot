namespace QuantowerPropBot.Core;

/// <summary>
/// One H1 OHLCV bar. TimeUtc is the bar's timestamp in UTC (bar open time, matching the
/// Python bot's convention — the Python engine uses the bar 'datetime' which is the open).
/// Keep everything double to mirror the Python float math exactly.
/// </summary>
public readonly record struct Bar(
    DateTime TimeUtc,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume);
