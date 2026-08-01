namespace QuantowerPropBot.Core;

public enum Side { Long, Short }

/// <summary>
/// A trade signal produced on an H1 bar close. Mirrors the Python `Signal` dataclass.
/// TP/SL are in points; they are anchored to the ACTUAL fill price at execution time,
/// NOT to EntryPrice (which is only the signal-bar close). See PORT_SPEC §5.
/// </summary>
public sealed record Signal(
    Side Side,
    double EntryPrice,   // signal-bar close (reference only)
    double TpPoints,
    double SlPoints,
    string Symbol,
    string Session);
