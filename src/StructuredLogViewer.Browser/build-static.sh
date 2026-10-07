#!/usr/bin/env bash
# Builds StructuredLogViewer.Browser as a self-contained static site, without the wasm-tools workload
# (build/NoWorkloadWasm.props fetches the WebAssembly toolchain packs from NuGet instead).
#   src/StructuredLogViewer.Browser/build-static.sh <outdir> [--target=pages|cloudflare] [--base=/binlog] [--headers=<file>]
# <outdir> receives the site root (index.html, _framework/, ...); upload its contents under any path.
#   --target   delivery (see tools/stage.mjs): pages (default; .br binaries decoded in the page) or cloudflare
#   --base     URL path the site is served under, used only for the Cloudflare _headers rules (default /<outdir name>)
#   --headers  Workers _headers file to append this app's rules to (cloudflare target)
# Needs the .NET 10 SDK, node 20+ and network access (NuGet, npm). Runs on ubuntu-latest as is.
set -euo pipefail

if [ $# -lt 1 ] || [[ "$1" == --* ]]; then sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 2; fi
out="$1"; shift
target=pages; stage_args=()
for a in "$@"; do
  case "$a" in
    --target=*) target="${a#--target=}" ;;
    --base=*|--headers=*) stage_args+=("$a") ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
case "$out" in "$here"|"$here"/*) echo "outdir must be outside src/StructuredLogViewer.Browser/" >&2; exit 2 ;; esac
cd "$here"

(cd tools && npm ci --no-audit --no-fund)

pub="$here/bin/Release/publish-static"
rm -rf "$pub"
dotnet publish StructuredLogViewer.Browser.csproj -c Release -o "$pub"

node tools/stage.mjs "$pub/wwwroot" "$out" --target="$target" ${stage_args[@]+"${stage_args[@]}"}
echo "Static site ready: $out"
