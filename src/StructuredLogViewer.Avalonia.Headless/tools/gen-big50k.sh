#!/usr/bin/env bash
# Generates a binlog with one node holding 50,000 children (the AddItem "I" item list), for repeatable
# tree benchmarks:  bash gen-big50k.sh <out.binlog>
# Then: dotnet run -c Release --project src/StructuredLogViewer.Avalonia.Headless -- --bench <out.binlog> <outDir> [--flat|--treeview]
set -euo pipefail
out=$(realpath -m "${1:-big50k.binlog}")
dir=$(mktemp -d)
{
  echo '<Project DefaultTargets="Build">'
  echo '  <ItemGroup>'
  seq 0 49999 | sed 's/.*/    <I Include="item&" \/>/'
  echo '  </ItemGroup>'
  echo '  <Target Name="Build"><Message Text="big50k" /></Target>'
  echo '</Project>'
} > "$dir/big.proj"
dotnet msbuild "$dir/big.proj" -nologo -v:q "-bl:$out" > /dev/null
rm -rf "$dir"
ls -l "$out"
