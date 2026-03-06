#!/bin/bash
# SpireBridge automated test suite using websocat
set -euo pipefail

WS="ws://127.0.0.1:38642/"
PASSED=0
FAILED=0

send() {
    local payload="$1"
    (echo "$payload"; sleep 1) | websocat "$WS" 2>/dev/null
}

check() {
    local name="$1" condition="$2"
    if [ "$condition" = "true" ]; then
        PASSED=$((PASSED + 1))
        echo "  ✅ $name"
    else
        FAILED=$((FAILED + 1))
        echo "  ❌ $name"
    fi
}

get_field() {
    echo "$1" | python3 -c "import sys,json; d=json.load(sys.stdin); print(eval('d$2'))" 2>/dev/null || echo ""
}

has_field() {
    echo "$1" | python3 -c "
import sys,json
d=json.load(sys.stdin)
try:
    v=eval('d$2')
    print('true' if v is not None else 'false')
except: print('false')
" 2>/dev/null || echo "false"
}

echo "============================================================"
echo "SpireBridge Automated Test Suite"
echo "============================================================"

# Test 1: Main Menu
echo ""
echo "🔹 Test 1: Main Menu State"
R=$(send '{"action":"get_state"}')
check "status ok" "$([ "$(get_field "$R" '["status"]')" = "ok" ] && echo true || echo false)"
check "screen is main_menu" "$([ "$(get_field "$R" '["data"]["screen"]')" = "main_menu" ] && echo true || echo false)"
check "not in run" "$([ "$(get_field "$R" '["data"]["in_run"]')" = "False" ] && echo true || echo false)"

# Test 2: Console Command
echo ""
echo "🔹 Test 2: Console Command"
R=$(send '{"action":"console","command":"help"}')
check "console returns ok" "$([ "$(get_field "$R" '["status"]')" = "ok" ] && echo true || echo false)"
echo "   Console response: $(echo "$R" | head -c 200)"

# Test 3: Error Handling
echo ""
echo "🔹 Test 3: Error Handling"
R=$(send '{"action":"nonexistent"}')
check "unknown action → error" "$([ "$(get_field "$R" '["status"]')" = "error" ] && echo true || echo false)"

R=$(send 'not json at all')
check "invalid JSON → error" "$([ "$(get_field "$R" '["status"]')" = "error" ] && echo true || echo false)"

# Test 4: Request ID echo
echo ""
echo "🔹 Test 4: Request ID Passthrough"
R=$(send '{"action":"get_state","id":"test-42"}')
check "id echoed" "$([ "$(get_field "$R" '["id"]')" = "test-42" ] && echo true || echo false)"

# Wait for run
echo ""
echo "⏳ Waiting for a run to start... (start Ironclad A0 in game)"
for i in $(seq 1 120); do
    R=$(send '{"action":"get_state"}')
    IN_RUN=$(get_field "$R" '["data"]["in_run"]')
    if [ "$IN_RUN" = "True" ]; then
        echo "   ✅ Run detected!"
        break
    fi
    sleep 1
done

R=$(send '{"action":"get_state"}')
SCREEN=$(get_field "$R" '["data"]["screen"]')
if [ "$SCREEN" != "combat" ]; then
    echo "   Not in combat (screen=$SCREEN). Some tests will be skipped."
fi

# Test 5: Combat State
echo ""
echo "🔹 Test 5: Combat State"
R=$(send '{"action":"get_state"}')
check "screen is combat" "$([ "$(get_field "$R" '["data"]["screen"]')" = "combat" ] && echo true || echo false)"
check "has player.hp" "$(has_field "$R" '["data"]["player"]["hp"]')"
check "has player.energy" "$(has_field "$R" '["data"]["player"]["energy"]')"
check "has player.hand" "$(has_field "$R" '["data"]["player"]["hand"]')"
check "has player.deck" "$(has_field "$R" '["data"]["player"]["deck"]')"
check "has player.relics" "$(has_field "$R" '["data"]["player"]["relics"]')"
check "has player.potions" "$(has_field "$R" '["data"]["player"]["potions"]')"
check "has combat.enemies" "$(has_field "$R" '["data"]["combat"]["enemies"]')"
HAND_LEN=$(echo "$R" | python3 -c "import sys,json; print(len(json.load(sys.stdin)['data']['player']['hand']))" 2>/dev/null || echo 0)
check "hand has cards ($HAND_LEN)" "$([ "$HAND_LEN" -gt 0 ] && echo true || echo false)"

# Card structure
echo "$R" | python3 -c "
import sys,json
d=json.load(sys.stdin)
card=d['data']['player']['hand'][0]
fields=['id','type','cost','can_play','target_type']
for f in fields:
    ok='✅' if f in card else '❌'
    print(f'  {ok} card has {f}')
enemy=d['data']['combat']['enemies'][0]
efields=['hp','max_hp','block','intents','index']
for f in efields:
    ok='✅' if f in enemy else '❌'
    print(f'  {ok} enemy has {f}')
" 2>/dev/null

# Test 6: Play Card
echo ""
echo "🔹 Test 6: Play Card"
# Find first playable attack
PLAY_CMD=$(echo "$R" | python3 -c "
import sys,json
d=json.load(sys.stdin)
hand=d['data']['player']['hand']
for i,c in enumerate(hand):
    if c.get('can_play'):
        cmd={'action':'play','card':i}
        if c.get('target_type')=='AnyEnemy': cmd['target']=0
        print(json.dumps(cmd))
        break
" 2>/dev/null)
if [ -n "$PLAY_CMD" ]; then
    R=$(send "$PLAY_CMD")
    check "play card" "$([ "$(get_field "$R" '["status"]')" = "ok" ] && echo true || echo false)"
    echo "   Response: $(echo "$R" | head -c 200)"
    sleep 1.5
else
    check "play card (no playable card)" "false"
fi

# Test 7: End Turn
echo ""
echo "🔹 Test 7: End Turn"
R=$(send '{"action":"end_turn"}')
check "end_turn" "$([ "$(get_field "$R" '["status"]')" = "ok" ] && echo true || echo false)"
sleep 2

R=$(send '{"action":"get_state"}')
SCREEN=$(get_field "$R" '["data"]["screen"]')
echo "   Screen after end turn: $SCREEN"

# Test 8: Kill All → Rewards
echo ""
echo "🔹 Test 8: Win Combat (kill all)"
# Make sure we're in combat
R=$(send '{"action":"get_state"}')
SCREEN=$(get_field "$R" '["data"]["screen"]')
if [ "$SCREEN" = "combat" ]; then
    R=$(send '{"action":"console","command":"kill all"}')
    check "kill all executed" "$([ "$(get_field "$R" '["status"]')" = "ok" ] && echo true || echo false)"
    sleep 3

    R=$(send '{"action":"get_state"}')
    SCREEN=$(get_field "$R" '["data"]["screen"]')
    echo "   Screen after kill: $SCREEN"
    check "post-combat screen" "$(echo "$SCREEN" | grep -qE 'rewards|card_reward|map' && echo true || echo false)"
else
    echo "   Already on $SCREEN, skipping kill"
fi

# Test 9: Navigate to map
echo ""
echo "🔹 Test 9: Map Navigation"
# Try to get to map by proceeding/skipping through rewards
for attempt in $(seq 1 10); do
    R=$(send '{"action":"get_state"}')
    SCREEN=$(get_field "$R" '["data"]["screen"]')
    if [ "$SCREEN" = "map" ]; then break; fi
    send '{"action":"proceed"}' > /dev/null 2>&1
    sleep 1
    send '{"action":"skip"}' > /dev/null 2>&1
    sleep 1
done

R=$(send '{"action":"get_state"}')
SCREEN=$(get_field "$R" '["data"]["screen"]')
echo "   Current screen: $SCREEN"
if [ "$SCREEN" = "map" ]; then
    check "map screen" "true"
    NODE_COUNT=$(echo "$R" | python3 -c "import sys,json; print(len(json.load(sys.stdin)['data']['map']['available_nodes']))" 2>/dev/null || echo 0)
    check "map has nodes ($NODE_COUNT)" "$([ "$NODE_COUNT" -gt 0 ] && echo true || echo false)"
else
    check "map screen" "false"
fi

# Test 10: Event via console
echo ""
echo "🔹 Test 10: Event (via console)"
R=$(send '{"action":"console","command":"event GOLDEN_IDOL"}')
check "event command" "$([ "$(get_field "$R" '["status"]')" = "ok" ] && echo true || echo false)"
sleep 2
R=$(send '{"action":"get_state"}')
SCREEN=$(get_field "$R" '["data"]["screen"]')
echo "   Screen after event cmd: $SCREEN"

# Summary
echo ""
echo "============================================================"
echo "Results: $PASSED passed, $FAILED failed"
echo "============================================================"
