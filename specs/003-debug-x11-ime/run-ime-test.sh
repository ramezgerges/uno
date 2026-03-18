#!/bin/bash
# Headless IME test script for Uno Platform X11/Skia backend
# Tests D-Bus IME (fcitx5) and XIM fallback in Xvfb environment
#
# Prerequisites: Xvfb, dbus-launch, fcitx5, fcitx5-remote, xdotool, fluxbox, dotnet
#
# Usage: ./run-ime-test.sh [--build]

set -euo pipefail

DISPLAY_NUM=42
APP_LOG="/tmp/uno-ime-test-app.log"
LOG_DIR="/tmp/ime-test"
TEST_RESULT=0
BUILD="${1:-}"

mkdir -p "$LOG_DIR"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log() { echo -e "${GREEN}[TEST]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
fail() { echo -e "${RED}[FAIL]${NC} $*"; TEST_RESULT=1; }
pass() { echo -e "${GREEN}[PASS]${NC} $*"; }

cleanup() {
    log "Cleaning up..."
    [ -n "${APP_PID:-}" ] && kill "$APP_PID" 2>/dev/null || true
    pkill -f "fcitx5" 2>/dev/null || true
    pkill -f "fluxbox" 2>/dev/null || true
    [ -n "${XVFB_PID:-}" ] && kill "$XVFB_PID" 2>/dev/null || true
    [ -n "${DBUS_SESSION_BUS_PID:-}" ] && kill "$DBUS_SESSION_BUS_PID" 2>/dev/null || true
    rm -f "/tmp/.X${DISPLAY_NUM}-lock" 2>/dev/null || true
    sleep 1
}
trap cleanup EXIT

take_screenshot() {
    local name="${1:-screenshot}"
    xwd -display ":$DISPLAY_NUM" -root -out "$LOG_DIR/${name}.xwd" 2>/dev/null \
        && echo "Screenshot saved: $LOG_DIR/${name}.xwd" \
        || true
}

# --- Build ---
if [ "$BUILD" = "--build" ]; then
    log "Building SamplesApp..."
    cd "$(dirname "$0")/../../src"
    dotnet build SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
        -p:UnoTargetFrameworkOverride=net10.0 --no-restore -q
    cd - > /dev/null
fi

# --- Start Xvfb ---
log "Starting Xvfb on :$DISPLAY_NUM..."
rm -f "/tmp/.X${DISPLAY_NUM}-lock" 2>/dev/null || true
pkill -f "Xvfb :$DISPLAY_NUM" 2>/dev/null || true
sleep 0.5
Xvfb ":$DISPLAY_NUM" -screen 0 1280x720x24 &
XVFB_PID=$!
sleep 1
export DISPLAY=":$DISPLAY_NUM"

# --- Start D-Bus ---
log "Starting D-Bus session..."
eval "$(dbus-launch --sh-syntax)"
log "D-Bus: $DBUS_SESSION_BUS_ADDRESS"

# --- Start fluxbox ---
log "Starting window manager..."
fluxbox &>/dev/null &
sleep 1

# --- Configure fcitx5 ---
FCITX_CONFIG_DIR="$HOME/.config/fcitx5"
mkdir -p "$FCITX_CONFIG_DIR/conf"
cat > "$FCITX_CONFIG_DIR/profile" << 'PROFILE'
[Groups/0]
Name=Default
Default Layout=us
DefaultIM=pinyin

[Groups/0/Items/0]
Name=keyboard-us
Layout=

[Groups/0/Items/1]
Name=pinyin
Layout=

[GroupOrder]
0=Default
PROFILE

start_app() {
    local script_dir
    script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    cd "$script_dir/../../src/SamplesApp/SamplesApp.Skia.Generic"
    > "$APP_LOG"
    dotnet run --no-build -p:UnoTargetFrameworkOverride=net10.0 > "$APP_LOG" 2>&1 &
    APP_PID=$!
    sleep 8
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        fail "SamplesApp did not start"
        tail -30 "$APP_LOG"
        return 1
    fi
    return 0
}

stop_app() {
    [ -n "${APP_PID:-}" ] && kill "$APP_PID" 2>/dev/null || true
    sleep 1
    APP_PID=""
}

# ==========================================================
# TEST 1: Fcitx5 D-Bus IME — English typing
# ==========================================================
log "=== Test 1: Fcitx5 D-Bus IME — English typing ==="

export GTK_IM_MODULE=fcitx
export XMODIFIERS="@im=fcitx"
unset UNO_IM_MODULE 2>/dev/null || true

fcitx5 -D --disable wayland,waylandim > "$LOG_DIR/fcitx5.log" 2>&1 &
sleep 2
fcitx5-remote -s keyboard-us
sleep 0.5

start_app || exit 1

# Verify IME detection
if grep -q "Creating Fcitx D-Bus IME client" "$APP_LOG"; then
    pass "Fcitx D-Bus IME detected"
else
    fail "Fcitx D-Bus IME NOT detected"
fi

if grep -q "Connected to fcitx5 D-Bus" "$APP_LOG"; then
    pass "Connected to fcitx5 D-Bus"
else
    fail "Failed to connect to fcitx5 D-Bus"
fi

# Click TextBox and type
xdotool mousemove 400 89 && sleep 0.3 && xdotool click 1
sleep 1
xdotool type --delay 150 "hello"
sleep 1

if grep -q "handled=False" "$APP_LOG"; then
    pass "English keys passed through IME (not handled)"
else
    fail "English key handling not found in logs"
fi

if grep -q "Dispatching KeyDown" "$APP_LOG"; then
    pass "KeyDown events dispatched"
else
    fail "KeyDown dispatch not found"
fi

take_screenshot "01-english-hello"

# ==========================================================
# TEST 2: Fcitx5 D-Bus IME — Pinyin commit
# ==========================================================
log "=== Test 2: Fcitx5 D-Bus IME — Pinyin commit ==="

fcitx5-remote -s pinyin
sleep 1

xdotool key n
sleep 0.3
xdotool key i
sleep 0.3
xdotool key space
sleep 1

if grep -q "CommitString: '你'" "$APP_LOG"; then
    pass "Pinyin commit delivered '你' via D-Bus CommitString signal"
else
    fail "Pinyin commit NOT found in logs"
fi

if grep -q "D-Bus IME Commit: '你'" "$APP_LOG"; then
    pass "Commit signal reached keyboard source"
else
    fail "Commit signal did not reach keyboard source"
fi

take_screenshot "02-pinyin-commit"

# ==========================================================
# TEST 3: Fcitx5 — IME toggle (back to English)
# ==========================================================
log "=== Test 3: IME toggle — back to English ==="

fcitx5-remote -s keyboard-us
sleep 0.5
xdotool type --delay 150 "abc"
sleep 0.5

if grep -q "Dispatching KeyDown: vk=A" "$APP_LOG"; then
    pass "English mode after toggle works"
else
    fail "English mode after toggle failed"
fi

take_screenshot "03-english-after-toggle"

# Stop app for next test
stop_app
pkill -f "fcitx5" 2>/dev/null || true
sleep 1

# ==========================================================
# TEST 4: UNO_IM_MODULE=none — XIM fallback
# ==========================================================
log "=== Test 4: UNO_IM_MODULE=none — XIM fallback ==="

export UNO_IM_MODULE=none
start_app || exit 1

if grep -q "IME explicitly disabled via UNO_IM_MODULE=none" "$APP_LOG"; then
    pass "IME disabled via UNO_IM_MODULE=none"
else
    fail "UNO_IM_MODULE=none not detected"
fi

# Type and verify normal keyboard works
xdotool mousemove 400 89 && sleep 0.3 && xdotool click 1
sleep 1
xdotool type --delay 150 "test"
sleep 1

if grep -q "Dispatching KeyDown" "$APP_LOG"; then
    pass "Keyboard works in XIM fallback mode"
else
    fail "Keyboard not working in XIM fallback mode"
fi

take_screenshot "04-xim-fallback"
stop_app

# ==========================================================
# Summary
# ==========================================================
echo ""
log "Screenshots in: $LOG_DIR/"
log "App log: $APP_LOG"
log "Fcitx5 log: $LOG_DIR/fcitx5.log"
echo ""
if [ $TEST_RESULT -eq 0 ]; then
    log "All tests passed!"
else
    fail "Some tests failed. Check output above."
fi

exit $TEST_RESULT
