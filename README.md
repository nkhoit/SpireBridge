# SpireBridge

WebSocket bridge mod for **Slay the Spire 2** — programmatic game control for AI agents.

## Overview

SpireBridge loads as a game mod and exposes a WebSocket server on `ws://127.0.0.1:38642/`. Clients connect and send JSON commands to read game state, play cards, navigate the map, and more.

## Installation

```bash
./scripts/deploy.sh
```

This builds the mod and copies `SpireBridge.dll` + `SpireBridge.pck` to the game's `mods/` directory.

**macOS game path:** `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/mods/`

## Protocol

All messages are JSON. Send a command, receive a response.

### Request Format

```json
{
  "action": "get_state",
  "id": "optional-request-id"
}
```

### Response Format

```json
{
  "status": "ok",
  "action": "get_state",
  "id": "optional-request-id",
  "data": { ... }
}
```

Errors:
```json
{
  "status": "error",
  "error": "error_code",
  "message": "Human-readable description"
}
```

## Actions

### `get_state`
Returns full game state: screen, player info, combat state, map.

### `play`
Play a card from hand.
```json
{"action": "play", "card_index": 0, "target_index": 0}
```
- `card_index` (required): Index in hand (0-based)
- `target_index` (optional): Enemy index for targeted cards. Defaults to first enemy.

### `end_turn`
End the current turn.
```json
{"action": "end_turn"}
```

### `use_potion`
Use a potion.
```json
{"action": "use_potion", "potion_index": 0, "target_index": 0}
```

### `choose_node`
Select a map node to travel to.
```json
{"action": "choose_node", "row": 1, "col": 2}
```

### Stubs (v0.1)
These actions are recognized but not yet implemented:
- `choose_card` — Card reward/selection screens
- `skip` — Skip card rewards
- `choose_option` — Event choices
- `proceed` — Advance past reward/rest screens
- `start_run` — Start a new run
- `abandon_run` — Abandon current run

## Game State Shape

```json
{
  "screen": "combat|map|main_menu|game_over",
  "in_run": true,
  "act": 1,
  "floor": 3,
  "player": {
    "character": "IronClad",
    "hp": 72,
    "max_hp": 80,
    "gold": 99,
    "energy": 3,
    "max_energy": 3,
    "block": 5,
    "hand": [
      {"id": "Strike", "type": "Attack", "cost": 1, "target_type": "AnyEnemy", "can_play": true}
    ],
    "draw_pile_count": 5,
    "discard_pile_count": 2,
    "powers": [{"id": "Strength", "amount": 2}],
    "deck": [...],
    "relics": [{"id": "BurningBlood"}],
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
  }
}
```

## Building

```bash
dotnet build
```

Requires .NET 9 SDK. Reference DLLs in `lib/` (not committed — copy from game).

## Architecture

- **SpireBridgeMod.cs** — Entry point, WebSocket server, main thread marshaling
- **CommandHandler.cs** — Routes JSON commands to handlers
- **StateReader.cs** — Reads and serializes game state
- **CombatActions.cs** — Card play, end turn, potion use
- **MapActions.cs** — Map node selection

All game API calls run on the main Godot thread via a Timer callback. WebSocket I/O runs on a background thread.

## License

MIT
