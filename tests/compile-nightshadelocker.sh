#!/usr/bin/env bash
# Compile NightshadeLocker against this repo's Carbon 2.0.262 and patched Rust assemblies.
# Defines: CARBON, RUST, OXIDE_PUBLICIZED, UNIX.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/tests/stubs/bin"
mkdir -p "$OUT"

build_stub() {
  local name="$1"
  local src="$2"
  shift 2
  local dir="/tmp/nsl-stubs/$name"
  rm -rf "$dir"
  mkdir -p "$dir"
  cp "$src" "$dir/Stub.cs"
  {
    echo '<Project Sdk="Microsoft.NET.Sdk">'
    echo '  <PropertyGroup>'
    echo '    <TargetFramework>netstandard2.0</TargetFramework>'
    echo '    <Nullable>disable</Nullable>'
    echo '    <ImplicitUsings>disable</ImplicitUsings>'
    echo "    <AssemblyName>$name</AssemblyName>"
    echo '  </PropertyGroup>'
    if [ "$#" -gt 0 ]; then
      echo '  <ItemGroup>'
      for ref in "$@"; do
        echo "    <Reference Include=\"$ref\"><HintPath>$OUT/$ref.dll</HintPath></Reference>"
      done
      echo '  </ItemGroup>'
    fi
    echo '</Project>'
  } > "$dir/$name.csproj"
  dotnet build "$dir/$name.csproj" -c Release -v q -o "$OUT"
}

build_stub UnityEngine.CoreModule "$ROOT/tests/stubs/Core/Core.cs"
build_stub UnityEngine.PhysicsModule "$ROOT/tests/stubs/Physics/Physics.cs" UnityEngine.CoreModule
build_stub UnityEngine.TextRenderingModule "$ROOT/tests/stubs/Text/Text.cs"
build_stub UnityEngine.UI "$ROOT/tests/stubs/UI/Scroll.cs" UnityEngine.CoreModule
build_stub Facepunch.UnityEngine "$ROOT/tests/stubs/Facepunch/Facepunch.cs" UnityEngine.CoreModule
build_stub Facepunch.System "$ROOT/tests/stubs/FacepunchSystem/System.cs"

dotnet build "$ROOT/tests/NightshadeLocker.Compile/Compile.csproj" -v q --nologo -p:WarningLevel=0
dotnet run --project "$ROOT/tests/NightshadeLocker.Tests/NightshadeLocker.Tests.csproj" -v q
