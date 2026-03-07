# SpireBridge

WebSocket bridge mod for **Slay the Spire 2** — programmatic game control for AI agents.

## Overview

SpireBridge loads as a game mod and exposes a WebSocket server on `ws://127.0.0.1:38642`. Clients connect and send JSON commands to read game state, play cards, navigate menus, and control runs. The mod also pushes state updates via events, so agents can react to game changes without polling.

**Architecture:** SpireBridge (C# mod) → Agent (Python/any language) → LLM

## Features

- **Full game state** — Cards with names, descriptions, damage, block, keywords, energy cost. Enemies with HP, intents, powers. Player HP, gold, energy, relics, potions, deck.
- **All screens** — Combat, map, events, rewards, card selection, shop, rest site, treasure, game over, main menu.
- **`available_actions`** — Every `get_state` response includes the list of valid actions with descriptions. Agents never need to guess what's possible.
- **Push events** — State updates broadcast on combat start, turn start, room changes, etc.
- **Console cheats** — Debug commands for testing (godmode, teleport, spawn enemies, give items).

## Quick Start

```bash
# Build
dotnet build -c Release -o out

# Deploy (macOS)
cp out/SpireBridge.dll "~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/"

# Deploy (Windows)
cp out/SpireBridge.dll "<Steam>/steamapps/common/Slay the Spire 2/mods/"

# Restart game, then connect
echo '{"action":"get_state"}' | websocat ws://127.0.0.1:38642
```

### Prerequisites

- .NET 9+ SDK
- Reference DLLs in `lib/` (copy from game install — not committed):
  - `sts2.dll` — Game assembly
  - `GodotSharp.dll` — Godot engine bindings
  - `0Harmony.dll` — Harmony patching library

## Protocol

All communication is JSON over WebSocket. Send a command, receive a response.

### Request / Response

```json
// Request
{"action": "get_state", "id": "1"}

// Success
{"status": "ok", "action": "get_state", "id": "1", "data": { ... }}

// Error
{"status": "error", "error": "error_code", "message": "Human-readable description"}
```

### Push Events

Broadcast to all connected clients on game events (no request needed):

```json
{"type": "state_update", "event": "turn_started", "seq": 42, "state": { ... }}
```

| Event | When |
|---|---|
| `run_started` | New run begins |
| `room_entered` / `room_exited` | Room transitions |
| `act_entered` | New act begins |
| `combat_start` / `combat_won` / `combat_ended` | Combat lifecycle |
| `turn_started` / `turn_ended` | Turn boundaries |
| `screen_changed` | Overlay pushed/popped |

## Actions

### State
| Action | Description |
|---|---|
| `get_state` | Full game state with `available_actions` |

### Run Management
| Action | Params | Description |
|---|---|---|
| `start_run` | `character?` | Start new run (handles full menu navigation) |
| `abandon_run` | — | Abandon current run |

### Combat
| Action | Params | Description |
|---|---|---|
| `play` | `card`, `target?` | Play card from hand (0-indexed) |
| `end_turn` | — | End player's turn |
| `use_potion` | `potion_index`, `target_index?` | Use a potion |
| `discard_potion` | `potion_index` | Discard a potion (available on any screen) |

### Navigation & Screens
| Action | Params | Description |
|---|---|---|
| `choose_node` | `row`, `col` | Select map node |
| `choose_reward` | `index` | Collect a reward |
| `choose_card` | `index` | Pick card from selection screen |
| `skip` | — | Skip card reward |
| `choose_option` | `index` | Choose event option |
| `choose_rest_option` | `index` | Choose rest site option (heal/smith) |
| `shop_buy` | `index` | Buy shop item |
| `open_chest` | — | Open treasure chest |
| `proceed` | — | Click proceed/leave button |
| `confirm` | — | Click confirm button on overlay |

### Debug
| Action | Params | Description |
|---|---|---|
| `console` | `command` | Execute dev console command |
| `debug_tree` | `path?`, `depth?` | Dump Godot scene tree |

## Game State

```json
{
  "screen": "combat",
  "available_actions": [
    {"action": "play", "card": 0, "description": "Play Strike (6 dmg)", "targets": [0, 1]},
    {"action": "play", "card": 2, "description": "Play Defend (5 block)"},
    {"action": "end_turn", "description": "End your turn"},
    {"action": "use_potion", "potion_index": 0, "description": "Use potion Fire Potion", "targets": [0, 1]},
    {"action": "discard_potion", "potion_index": 0, "description": "Discard Fire Potion"}
  ],
  "player": {
    "character": "CHARACTER.IRONCLAD",
    "hp": 72, "max_hp": 80,
    "gold": 99, "energy": 3, "block": 5,
    "hand": [{
      "id": "STRIKE_IRONCLAD", "name": "Strike", "type": "Attack",
      "cost": 1, "target_type": "AnyEnemy", "can_play": true,
      "damage": 6, "block": null, "rarity": "Basic", "upgraded": false,
      "is_x_cost": false, "keywords": null,
      "description": "Deal 6 damage."
    }],
    "deck": [{ "..." : "same card shape" }],
    "relics": [{"id": "RELIC.BURNING_BLOOD", "name": "Burning Blood", "description": "At the end of combat, heal 6 HP."}],
    "potions": [{"slot": 0, "id": "FIRE_POTION", "name": "Fire Potion", "target_type": "AnyEnemy", "can_use": true}],
    "powers": [{"id": "STRENGTH_POWER", "name": "Strength", "amount": 2, "type": "Buff"}],
    "draw_pile_count": 5, "discard_pile_count": 2, "exhaust_pile_count": 0
  },
  "combat": {
    "is_player_turn": true,
    "enemies": [{
      "index": 0, "id": "JawWorm", "name": "Jaw Worm",
      "hp": 40, "max_hp": 44, "block": 0,
      "is_alive": true, "is_hittable": true,
      "powers": [],
      "intents": [{"type": "Attack", "damage": 11, "hits": 1}]
    }]
  },
  "map": {
    "available_nodes": [
      {"row": 3, "col": 0, "type": "Monster"},
      {"row": 3, "col": 1, "type": "RestSite"}
    ]
  },
  "rewards": [
    {"index": 0, "type": "GoldReward", "gold": 25},
    {"index": 1, "type": "PotionReward", "potion_id": "FIRE_POTION", "name": "Fire Potion"},
    {"index": 2, "type": "RelicReward", "name": "Bag of Preparation"},
    {"index": 3, "type": "CardReward"}
  ],
  "shop": {
    "items": [
      {"index": 0, "type": "Card", "name": "Iron Wave", "cost": 49, "affordable": true},
      {"index": 5, "type": "Relic", "name": "Vajra", "cost": 150, "affordable": false},
      {"index": 9, "type": "CardRemoval", "cost": 75, "affordable": true}
    ]
  },
  "rest_site": {
    "options": [
      {"index": 0, "id": "HEAL", "name": "Rest", "description": "Heal for 30% of your Max HP (24)."},
      {"index": 1, "id": "SMITH", "name": "Smith", "description": "Upgrade a card in your Deck."}
    ]
  },
  "event": {
    "event_id": "ABYSSAL_BATHS",
    "options": [
      {"index": 0, "text": "Enter the baths.", "locked": false, "is_proceed": false}
    ]
  }
}
```

### Screen Types

| Screen | Description | Key Actions |
|---|---|---|
| `main_menu` | Main menu | `start_run` |
| `combat` | Active combat | `play`, `end_turn`, `use_potion` |
| `map` | Map node selection | `choose_node` |
| `event` | Event with choices | `choose_option` |
| `rewards` | Post-combat rewards | `choose_reward`, `proceed` |
| `card_reward` | Card selection from reward | `choose_card`, `skip` |
| `card_select` | Card selection (smith/event) | `choose_card` |
| `shop` | Merchant shop | `shop_buy`, `proceed` |
| `rest_site` | Campfire | `choose_rest_option` |
| `treasure` | Treasure room | `open_chest`, `proceed` |
| `game_over` | Run ended | `start_run` |

### Card Keywords

Cards may include: `Exhaust`, `Ethereal`, `Retain`, `Innate`, `Sly`, `Eternal`, `Unplayable`

### Characters

`Ironclad`, `Silent`, `Defect`, `Regent`, `Necrobinder`, `Deprived`

## Console Commands

Debug commands via `{"action": "console", "command": "..."}`:

| Command | Example | Description |
|---|---|---|
| `godmode` | `godmode` | Toggle invincibility |
| `kill [all]` | `kill all` | Kill enemies |
| `win` | `win` | Win current combat |
| `gold <N>` | `gold 999` | Set gold |
| `room <Type>` | `room RestSite` | Teleport to room (PascalCase) |
| `fight <ID>` | `fight KNIGHTS_ELITE` | Start encounter (SCREAMING_SNAKE) |
| `event <ID>` | `event ABYSSAL_BATHS` | Jump to event |
| `potion <ID>` | `potion FIRE_POTION` | Give potion |
| `card <ID>` | `card BASH` | Add card to deck |
| `relic add <ID>` | `relic add VAJRA` | Give relic |
| `damage <N>` | `damage 40` | Deal damage to player |
| `heal <N>` | `heal 50` | Heal player |
| `energy <N>` | `energy 5` | Set energy |
| `draw <N>` | `draw 3` | Draw cards |
| `power <ID> <N> <T>` | `power STRENGTH_POWER 5 0` | Give power |

## Source Files

| File | Purpose |
|---|---|
| `SpireBridgeMod.cs` | Entry point, WebSocket server, main thread marshaling |
| `CommandHandler.cs` | JSON command routing |
| `StateReader.cs` | Game state serialization + `available_actions` |
| `CombatActions.cs` | Card play, end turn, potion use/discard |
| `MapActions.cs` | Map node navigation |
| `ScreenActions.cs` | Rewards, card selection, events, shop, proceed/skip |
| `RunActions.cs` | Start/abandon run (async menu navigation) |
| `GameEventBridge.cs` | Push-based state events via WebSocket |
| `ConsoleAction.cs` | Dev console command execution |

## Design Decisions

- **Structured JSON over screenshots** — Cheaper and better for LLM reasoning than vision
- **`available_actions` on every state** — Agents never guess; valid actions are always enumerated
- **Push events + pull state** — Events for reactivity, `get_state` for full context
- **Validation in mod** — Invalid actions return errors with codes; agents can retry
- **Menu navigation abstracted** — `start_run`/`abandon_run` handle full UI choreography
- **Single cross-platform DLL** — .NET IL works on both macOS and Windows
- **Main thread marshaling** — All game API calls run on Godot's main thread via Timer callback

## License

MIT
