#!/usr/bin/env python3
"""SpireBridge interactive test client."""
import asyncio
import json
import sys
import websockets

WS_URL = "ws://127.0.0.1:38642/"

async def send(ws, action: dict):
    await ws.send(json.dumps(action))
    resp = json.loads(await ws.recv())
    return resp

def print_state(state):
    d = state.get("data", state)
    screen = d.get("screen", "unknown")
    print(f"\n{'='*60}")
    print(f"Screen: {screen}")
    
    if d.get("in_run"):
        p = d.get("player", {})
        print(f"  {p.get('character','?')} | HP: {p.get('hp')}/{p.get('max_hp')} | Gold: {p.get('gold')} | Floor: {d.get('floor')}")
        relics = [r['id'] for r in p.get('relics', []) if r]
        if relics:
            print(f"  Relics: {', '.join(relics)}")
        potions = [po['id'] if po else '(empty)' for po in p.get('potions', [])]
        print(f"  Potions: {', '.join(potions)}")
    
    if screen == "combat":
        c = d.get("combat", {})
        p = d.get("player", {})
        print(f"  Energy: {p.get('energy')}/{p.get('max_energy')} | Block: {p.get('block')}")
        print(f"  Hand ({len(p.get('hand', []))}):")
        for i, card in enumerate(p.get("hand", [])):
            playable = "✓" if card.get("can_play") else "✗"
            print(f"    [{i}] {card['id']} ({card['type']}, cost {card['cost']}) {playable}")
        print(f"  Enemies:")
        for e in c.get("enemies", []):
            intents = ", ".join(f"{i['type']}" + (f" {i.get('damage','?')}x{i.get('hits',1)}" if i['type']=='Attack' else "") for i in e.get("intents", []))
            print(f"    [{e['index']}] {e.get('name','?')} HP:{e['hp']}/{e['max_hp']} Block:{e['block']} → {intents}")
        powers = p.get("powers", [])
        if powers:
            print(f"  Powers: {powers}")
    
    elif screen == "map":
        nodes = d.get("map", {}).get("available_nodes", [])
        print(f"  Available nodes:")
        for i, n in enumerate(nodes):
            print(f"    [{i}] Row {n['row']}, Col {n['col']} — {n['type']}")
    
    elif screen in ("card_reward", "card_select"):
        print(f"  (Card selection screen — use choose_card/skip)")
    
    elif screen == "rewards":
        print(f"  (Rewards screen — use choose_reward/proceed)")
    
    elif screen == "shop":
        print(f"  (Shop screen)")
    
    elif screen == "rest_site":
        print(f"  (Rest site — use rest/smith)")
    
    elif screen == "event":
        print(f"  (Event — use choose_option)")
    
    elif screen == "game_over":
        print(f"  (Game over — use proceed)")
    
    print(f"{'='*60}")

def print_help():
    print("""
Commands:
  state / s          — get current state
  play <card> [target] — play card index, optional target index
  end / e            — end turn  
  potion <idx> [target] — use potion
  node <idx>         — choose map node
  card <idx>         — choose card reward
  skip               — skip card reward
  reward <idx>       — choose reward
  option <idx>       — choose event option
  rest               — rest at campfire
  smith              — smith at campfire
  proceed / p        — proceed (rewards, game over)
  start [char] [asc] — start run (default: Ironclad, A0)
  abandon            — abandon run
  raw <json>         — send raw JSON
  quit / q           — exit
""")

async def main():
    print("Connecting to SpireBridge...")
    try:
        async with websockets.connect(WS_URL) as ws:
            print("Connected! Type 'help' for commands.\n")
            
            # Auto-fetch state
            resp = await send(ws, {"action": "get_state"})
            print_state(resp)
            
            while True:
                try:
                    line = await asyncio.get_event_loop().run_in_executor(None, lambda: input("\n> "))
                except EOFError:
                    break
                
                parts = line.strip().split()
                if not parts:
                    continue
                
                cmd = parts[0].lower()
                
                if cmd in ("quit", "q"):
                    break
                elif cmd == "help":
                    print_help()
                    continue
                elif cmd in ("state", "s"):
                    action = {"action": "get_state"}
                elif cmd == "play":
                    action = {"action": "play", "card": int(parts[1])}
                    if len(parts) > 2:
                        action["target"] = int(parts[2])
                elif cmd in ("end", "e"):
                    action = {"action": "end_turn"}
                elif cmd == "potion":
                    action = {"action": "use_potion", "potion": int(parts[1])}
                    if len(parts) > 2:
                        action["target"] = int(parts[2])
                elif cmd == "node":
                    action = {"action": "choose_node", "index": int(parts[1])}
                elif cmd == "card":
                    action = {"action": "choose_card", "index": int(parts[1])}
                elif cmd == "skip":
                    action = {"action": "skip"}
                elif cmd == "reward":
                    action = {"action": "choose_reward", "index": int(parts[1])}
                elif cmd == "option":
                    action = {"action": "choose_option", "index": int(parts[1])}
                elif cmd == "rest":
                    action = {"action": "rest"}
                elif cmd == "smith":
                    action = {"action": "smith"}
                elif cmd in ("proceed", "p"):
                    action = {"action": "proceed"}
                elif cmd == "start":
                    action = {"action": "start_run"}
                    if len(parts) > 1:
                        action["character"] = parts[1]
                    if len(parts) > 2:
                        action["ascension"] = int(parts[2])
                elif cmd == "abandon":
                    action = {"action": "abandon_run"}
                elif cmd == "raw":
                    action = json.loads(" ".join(parts[1:]))
                else:
                    print(f"Unknown command: {cmd}. Type 'help'.")
                    continue
                
                try:
                    resp = await send(ws, action)
                    if resp.get("status") == "ok" and "data" in resp:
                        print_state(resp)
                    else:
                        print(json.dumps(resp, indent=2))
                except Exception as ex:
                    print(f"Error: {ex}")
                    
    except ConnectionRefusedError:
        print("Could not connect — is STS2 running with SpireBridge?")
    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    asyncio.run(main())
