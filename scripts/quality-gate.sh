#!/usr/bin/env bash
# Local quality gate — the same checks CI runs. Every commit must pass this.
# Usage: ./scripts/quality-gate.sh
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || DOTNET="$HOME/.dotnet/dotnet"

step() { printf '\n\033[1;34m==> %s\033[0m\n' "$1"; }

step "1/5 Restore (NuGet audit: known-vulnerable packages fail the restore)"
"$DOTNET" restore

step "2/5 Format check"
"$DOTNET" format --verify-no-changes --no-restore

step "3/5 Build (analyzers on, warnings as errors)"
"$DOTNET" build --no-restore -c Release -warnaserror

step "4/5 Tests + coverage"
rm -rf TestResults
"$DOTNET" test --no-build -c Release --collect:"XPlat Code Coverage" --results-directory TestResults

step "4b/5 Coverage threshold (NFR-7: >= 80% lines on Domain + Application)"
python3 - "${COVERAGE_MIN:-80}" <<'PY'
import glob, sys, xml.etree.ElementTree as ET
minimum = float(sys.argv[1])
hit = {}
for f in glob.glob("TestResults/*/coverage.cobertura.xml"):
    for pkg in ET.parse(f).getroot().iter("package"):
        name = pkg.get("name")
        if name not in ("UrlShortener.Domain", "UrlShortener.Application"):
            continue
        for cls in pkg.iter("class"):
            for line in cls.iter("line"):
                key = (name, cls.get("filename"), line.get("number"))
                hit[key] = hit.get(key, False) or int(line.get("hits")) > 0
failed = False
for name in ("UrlShortener.Domain", "UrlShortener.Application"):
    lines = [v for (n, _, _), v in hit.items() if n == name]
    pct = 100 * sum(lines) / len(lines) if lines else 0
    print(f"{name}: {pct:.1f}% ({sum(lines)}/{len(lines)} lines)")
    failed |= pct < minimum
sys.exit(1 if failed else 0)
PY

step "5/5 Vulnerable dependency scan"
report=$("$DOTNET" list package --vulnerable --include-transitive)
echo "$report"
if echo "$report" | grep -q "has the following vulnerable packages"; then
  echo "FAILED: vulnerable packages found" >&2
  exit 1
fi

printf '\n\033[1;32mQuality gate passed.\033[0m\n'
