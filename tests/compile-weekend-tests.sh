#!/bin/sh
# Compiles the Eastern weekend clock, TruePVE toggle, and excavator rate tests.
# Defines match a Carbon server compile. NIGHTSHADE_RAID_TESTS drops the
# RustPlugin type, which needs Unity assemblies this repo does not ship.
# Newtonsoft.Json.dll is not in the repo; the script downloads the net45 build
# used only to compile these tests. It is not a plugin shim.
set -e
ROOT="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
CACHE="${TMPDIR:-/tmp}/nightshade-newtonsoft"
DLL="$CACHE/Newtonsoft.Json.dll"
if [ ! -f "$DLL" ]; then
    mkdir -p "$CACHE"
    PKG="$CACHE/newtonsoft.nupkg"
    curl -fsSL -o "$PKG" https://www.nuget.org/api/v2/package/Newtonsoft.Json/13.0.3
    unzip -p "$PKG" lib/net45/Newtonsoft.Json.dll > "$DLL"
fi
OUT="${TMPDIR:-/tmp}/NightshadeWeekendTests.exe"
mcs -sdk:4.5 -define:NIGHTSHADE_RAID_TESTS,CARBON,RUST,OXIDE_PUBLICIZED,UNIX \
    -r:"$DLL" \
    -out:"$OUT" \
    "$ROOT/carbon/plugins/03_Protection/NightshadeRaidProtection.cs" \
    "$ROOT/tests/NightshadeWeekendTests.cs"
cp "$DLL" "$(dirname "$OUT")/Newtonsoft.Json.dll"
mono "$OUT" "$ROOT/carbon/configs/TruePVE.json"
