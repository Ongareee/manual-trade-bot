# Manual Trade Bot

A read-only Quantower strategy that uses the machine's existing Rithmic data connection to generate manual trade instructions. It has no order, position, account-management, or broker-execution capability.

## Strategy flow

- SMA (MNQ and MGC): seed the validated 7/30/90/180 H1 engine, evaluate a non-mutating preview during the final five minutes, alert once when price approaches the solved stack+buffer entry from the opposite side, and send the entry instruction only when the closed H1 bar passes every original rule.
- ORB (MES and MNQ): use the validated opening-range width guard and break/retest/re-break state machine. Alert once while an armed re-break approaches its boundary from the correct side; send the entry instruction when live price reaches it.
- A pending market instruction expires after price moves 10 points beyond its reference entry in the intended direction without a reported fill.

## Replies

- `4100.25 SMA` — record the nearest pending SMA instruction as filled.
- `skip ORB` — skip the newest pending ORB instruction.
- `4112.50 manual exit SMA` — close the newest active SMA manual trade with the supplied reason.
- Exit reasons can be `TP`, `SL`, `manual exit`, `session end`, or another short description.

All setup deduplication and manual trades persist under `C:\trading\manual-trade-bot`.

## Telegram configuration

Copy `telegram.example.json` to `C:\trading\manual-trade-bot\telegram.json` and fill in the dedicated bot token and chat IDs. The two chat IDs may be the same. Configure a custom notification sound for this bot's chat in Telegram; bots cannot bypass device Focus/Do Not Disturb.

## Build and installation

```powershell
dotnet run --project .\tests\ManualTradeBot.Tests\ManualTradeBot.Tests.csproj
dotnet build .\src\ManualTradeBot.Quantower\ManualTradeBot.Quantower.csproj -c Release
```

Copy both release DLLs to `C:\Quantower\Settings\Scripts\Strategies\ManualTradeBot\`, then load and run **Manual Trade Bot (Read Only)** in Quantower.

## Safety invariant

CI/local safety validation should return no matches:

```powershell
rg "PlaceOrder|PlaceOrders|ClosePosition|CancelOrder|Accounts|Positions|Orders|OrderType|TradingEnabled" src -g "*.cs"
```
