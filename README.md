# SpireBridge

WebSocket bridge mod for **Slay the Spire 2** — programmatic game control for AI agents.

## Overview

SpireBridge loads as a game mod and exposes a WebSocket server on `ws://127.0.0.1:38642/`. Clients connect and send JSON commands to read game state, play cards, navigate menus, and control runs. The mod also pushes state updates via events, so agents can react to game changes without polling.

**Architecture:** SpireBridge (C# mod) → Agent (Python/any language) → LLM

## Installation

### Prerequisites
- .NET 9+ SDK
- Reference DLLs in `lib/` (copy from game — not committed):
  - `sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`

### Build & Deploy

```bash
dotnet build -c Release -o out
cp out/SpireBridge.dll "~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/"
```

The game must be restarted after deploying a new build. The game log will show `RUNNING MODDED! — Loaded 1 mods` on success.

**macOS mod path:** `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`

**Windows mod path:** `<Steam>/steamapps/common/Slay the Spire 2/mods/`

## Protocol

All communication is JSON over WebSocket. Send a command, receive a response. The mod also pushes unsolicited state updates on game events.

### Request

```json
{
  "action": "get_state",
  "id": "optional-request-id"
}
```

### Success Response

```json
{
  "status": "ok",
  "action": "get_state",
  "id": "optional-request-id",
  "data": { ... }
}
```

### Error Response

```json
{
  "status": "error",
  "error": "error_code",
  "message": "Human-readable description"
}
```

### Push Events

The mod pushes state updates when significant game events occur. These are sent to all connected clients without a request.

```json
{
  "type": "state_update",
  "event": "turn_started",
  "seq": 42,
  "state": { ... }
}
```

**Event types:**
| Event | When |
|---|---|
| `run_started` | New run begins |
| `room_entered` | Player enters a new room |
| `room_exited` | Player leaves a room |
| `act_entered` | New act begins |
| `combat_start` | Combat encounter starts |
| `combat_won` | All enemies defeated |
| `combat_ended` | Combat fully resolved |
| `turn_started` | Player's turn begins |
| `turn_ended` | Player's turn ends |
| `screen_changed` | Overlay screen pushed/popped (rewards, card selection, etc.) |

## Actions

### Game State

#### `get_state`
Returns the full game state. Always available.

```json
{"action": "get_state"}
```

### Run Management

#### `start_run`
Starts a new run from the main menu. Handles the full menu navigation: abandons any existing run → Singleplayer → Standard → Character Select → Confirm.

```json
{"action": "start_run", "character": "Ironclad"}
```

- `character` (optional): Character ID. Default: `"Ironclad"`. Falls back to first unlocked character.
- Returns immediately with `"status": "navigating"` — the navigation is async. Poll `get_state` for screen changes.

#### `abandon_run`
Abandons the current run (works from in-game). Opens Options → Abandon → Confirm.

```json
{"action": "abandon_run"}
```

### Combat

#### `play`
Play a card from hand.

```json
{"action": "play", "card": 0, "target": 0}
```

- `card` (required): Hand index (0-based). Also accepts `card_index`.
- `target` (optional): Enemy index for targeted cards (`target_type: "AnyEnemy"`).

#### `end_turn`
End the player's turn.

```json
{"action": "end_turn"}
```

#### `use_potion`
Use a potion from the potion belt.

```json
{"action": "use_potion", "potion": 0, "target": 0}
```

- `potion` (required): Potion slot index. Also accepts `potion_index`.
- `target` (optional): Enemy index for targeted potions.

### Map Navigation

#### `choose_node`
Select a map node to travel to. Must be an available (connected) node.

```json
{"action": "choose_node", "row": 1, "col": 2}
```

### Screen Interactions

These handle non-combat screens: rewards, card selection, events, etc.

#### `choose_reward`
Click a reward button on the rewards screen.

```json
{"action": "choose_reward", "index": 0}
```

- `index` (required): Reward button index (0-based). Opening a card reward transitions to the card selection screen.

#### `choose_card`
Select a card from a card selection screen (post-combat reward or event-based).

```json
{"action": "choose_card", "index": 0}
```

- `index` (required): Card index (0-based), scoped to the current overlay.

#### `skip`
Skip a card reward (clicks the skip/singing bowl button).

```json
{"action": "skip"}
```

#### `choose_option`
Select an event option.

```json
{"action": "choose_option", "index": 0}
```

- `index` (required): Option index (0-based). Works for both choices and the "proceed/leave" button.

#### `proceed`
Click the proceed button on the current overlay screen (rewards, rest site, etc.).

```json
{"action": "proceed"}
```

### Debug

#### `console`
Execute a dev console command. Only available when mods are loaded.

```json
{"action": "console", "command": "kill all"}
```

**Useful commands:** `kill [all]`, `win`, `gold <N>`, `event <id>`, `travel`, `card <ID> [pile]`, `relic [add|remove] <id>`, `energy <N>`, `damage <N>`, `draw <N>`, `die`, `act <N>`

#### `debug_tree`
Dump the Godot scene tree for debugging.

```json
{"action": "debug_tree", "path": "/root/Game", "depth": 3}
```

## Game State Shape

```json
{
  "screen": "combat",
  "seq": 42,
  "in_run": true,
  "act": 1,
  "floor": 3,
  "player": {
    "character": "CHARACTER.IRONCLAD",
    "hp": 72,
    "max_hp": 80,
    "gold": 99,
    "energy": 3,
    "max_energy": 3,
    "block": 5,
    "hand": [
      {
        "id": "STRIKE_IRONCLAD",
        "type": "Attack",
        "cost": 1,
        "target_type": "AnyEnemy",
        "can_play": true
      }
    ],
    "draw_pile_count": 5,
    "discard_pile_count": 2,
    "powers": [{"id": "Strength", "amount": 2}],
    "deck": [{"id": "STRIKE_IRONCLAD", "type": "Attack", "cost": 1}],
    "relics": [{"id": "RELIC.BURNING_BLOOD"}],
    "potions": [{"slot": 0, "id": "FirePotion", "target_type": "AnyEnemy"}]
  },
  "combat": {
    "is_player_turn": true,
    "enemies": [
      {
        "index": 0,
        "id": "JawWorm",
        "name": "Jaw Worm",
        "hp": 40,
        "max_hp": 44,
        "block": 0,
        "is_hittable": true,
        "powers": [],
        "intents": [{"type": "Attack", "damage": 11, "hits": 1}]
      }
    ]
  },
  "map": {
    "current_coord": {"row": 2, "col": 1},
    "visited_count": 3,
    "available_nodes": [
      {"row": 3, "col": 0, "type": "Monster"},
      {"row": 3, "col": 1, "type": "RestSite"}
    ]
  },
  "event": {
    "event_id": "NEOW",
    "options": [
      {"index": 0, "text": "Choose a card", "locked": false, "is_proceed": false}
    ]
  },
  "rewards": [
    {"index": 0, "type": "GoldReward", "gold": 25},
    {"index": 1, "type": "CardReward"}
  ]
}
```

### Screen Types

| Screen | Description |
|---|---|
| `main_menu` | Main menu (start/continue/abandon) |
| `combat` | Active combat encounter |
| `map` | Map screen (node selection) |
| `event` | Event room with choices |
| `rewards` | Post-combat rewards |
| `card_reward` | Card selection from combat reward |
| `card_select` | Card selection (event/Neow) |
| `shop` | Shop screen |
| `rest_site` | Campfire/rest site |
| `game_over` | Run ended (death or victory) |

## Source Files

| File | Purpose |
|---|---|
| `SpireBridgeMod.cs` | Entry point, WebSocket server, main thread marshaling via Godot Timer |
| `CommandHandler.cs` | JSON command routing |
| `StateReader.cs` | Reads and serializes full game state |
| `CombatActions.cs` | Card play, end turn, potion use |
| `MapActions.cs` | Map node selection (ForceClick on NMapPoint) |
| `ScreenActions.cs` | Rewards, card selection, events, proceed/skip |
| `RunActions.cs` | Start/abandon run (async menu navigation) |
| `GameEventBridge.cs` | Push-based state events via WebSocket |
| `ConsoleAction.cs` | Dev console command execution via reflection |

## Design Decisions

- **Text-based state, not screenshots** — Structured JSON is cheaper and better for LLM reasoning
- **Turn-level granularity** — Batch actions per turn, not per frame
- **Push-based events** — GameEventBridge broadcasts state on game events; agents don't need to poll
- **Validation in mod** — Invalid actions return errors; agents can retry
- **Menu navigation abstracted** — `start_run`/`abandon_run` handle full UI choreography
- **Single cross-platform DLL** — .NET IL works on both macOS and Windows
- **Main thread marshaling** — All game API calls run on Godot's main thread via Timer callback

## Tools

- `tools/test_client.py` — Interactive Python REPL client
- `tools/test_suite.sh` — Automated bash test suite (uses `websocat`)

## License

MIT
