#!/bin/bash
# ACSP - Air Cargo Scheduling (Derigs & Friederichs 2013)
# Double-click launcher: installs what is missing, starts the app, opens the browser.
set -e

# locate the project: next to this script, or the default ~/derigs checkout
DIR="$(cd "$(dirname "$0")" && pwd)"
if [ -f "$DIR/Acsp.sln" ]; then PROJECT="$DIR"
elif [ -f "$HOME/derigs/Acsp.sln" ]; then PROJECT="$HOME/derigs"
else
  echo "ERROR: Acsp.sln not found next to this script nor in ~/derigs."
  read -r -p "Press enter to close..."
  exit 1
fi
cd "$PROJECT"
echo "== ACSP launcher =="
echo "project: $PROJECT"

# 1) .NET 8 SDK (installed to ~/.dotnet without admin rights if missing)
if command -v dotnet >/dev/null 2>&1; then DOTNET="dotnet"
elif [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET="$HOME/.dotnet/dotnet"
else
  echo "Installing .NET 8 SDK into ~/.dotnet (one time, a couple of minutes)..."
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
  DOTNET="$HOME/.dotnet/dotnet"
fi
echo "dotnet: $($DOTNET --version)"

# 2) HiGHS solver library: bundled copy first, then Homebrew locations
if [ -f "$PROJECT/lib/libhighs.dylib" ]; then
  export ACSP_LIBHIGHS="$PROJECT/lib/libhighs.dylib"
  echo "HiGHS: bundled ($ACSP_LIBHIGHS)"
elif [ -f /opt/homebrew/lib/libhighs.dylib ] || [ -f /usr/local/lib/libhighs.dylib ]; then
  echo "HiGHS: system installation found"
elif command -v brew >/dev/null 2>&1; then
  echo "Installing HiGHS via Homebrew..."
  brew install highs
else
  echo "WARNING: HiGHS not found and Homebrew unavailable; the solver will not start."
  echo "Install Homebrew (https://brew.sh) and run: brew install highs"
  read -r -p "Press enter to close..."
  exit 1
fi

# macOS quarantine on the bundled dylib (harmless if the attribute is absent)
[ -n "$ACSP_LIBHIGHS" ] && xattr -d com.apple.quarantine "$ACSP_LIBHIGHS" 2>/dev/null || true

# 3) start the web app and open the browser once it answers
echo "Starting ACSP at http://localhost:5170 ..."
"$DOTNET" run --project src/Acsp.Web -c Release --no-launch-profile &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null' EXIT
for _ in $(seq 1 180); do
  curl -s -o /dev/null http://localhost:5170 && break
  sleep 1
done
open "http://localhost:5170"
echo ""
echo "ACSP is running. Keep this window open; press Ctrl+C (or close it) to stop the app."
wait $SERVER_PID
