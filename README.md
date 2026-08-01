# Manual Trade Bot

Signal-only companion for Quantower/Rithmic data. It never creates, modifies, or cancels broker orders.

It watches the same SMA and ORB setup rules as the Quantower strategy and sends:

1. a preparation alert at the configured approach distance;
2. a market-entry instruction when the setup becomes valid;
3. a persisted manual-trade workflow accepting `price STRATEGY`, `skip STRATEGY`, and `exit-price reason STRATEGY`.

The Quantower adapter is deliberately separate from execution code: it can subscribe to data only.

## Telegram notifications

Set a distinct notification sound/importance for the prep and entry chats in the Telegram client. Bots cannot override device Do Not Disturb or assign a message-level notification priority.

## Safety invariant

There is no broker SDK reference and no order function in this project. Quantower is a market-data source only.
