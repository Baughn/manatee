#!/usr/bin/env bash
# Standing benchmark harness for manatee-core (testing-strategy.md Benchmarks).
# Mirrors sparky's discipline: BenchmarkDotNet + MemoryDiagnoser, and a jj-aware
# compare-against-parent (@ vs @-). Kept deliberately simple.
#
#   scripts/bench.sh smoke  [filter]   # one in-process Dry iteration per suite (fast CI-ish check)
#   scripts/bench.sh run    [filter]   # full out-of-process run (accurate; minutes-to-hours)
#   scripts/bench.sh compare [filter]  # run @ and @-, diff the two BenchmarkDotNet CSVs
#
# `filter` is a BenchmarkDotNet glob, default '*'. Runs inside `nix develop`.
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root_dir/core/Manatee.Benchmarks/Manatee.Benchmarks.csproj"
results_dir="$root_dir/BenchmarkDotNet.Artifacts/results"

command="${1:-smoke}"
filter="${2:-*}"

dn() { nix develop "$root_dir" -c "$@"; }

latest_csv() { ls -t "$results_dir"/*-report.csv 2>/dev/null | head -n1 || true; }

case "$command" in
  smoke)
    dn dotnet run -c Release --project "$project" -- --smoke --filter "$filter"
    ;;

  run)
    dn dotnet run -c Release --project "$project" -- --filter "$filter"
    echo "Latest CSV: $(latest_csv)"
    ;;

  compare)
    # Bench @ (current), stash the CSVs, bench @- (parent), then diff. jj-aware:
    # `jj new @-` edits the parent in the working copy; `jj undo` restores @.
    tmp="$(mktemp -d)"
    echo ">> benchmarking @ (current change)"
    dn dotnet run -c Release --project "$project" -- --filter "$filter"
    cur="$(latest_csv)"; cp "$cur" "$tmp/new.csv"

    echo ">> switching to @- (parent)"
    dn jj new @-
    trap 'dn jj undo >/dev/null 2>&1 || true' EXIT
    dn dotnet run -c Release --project "$project" -- --filter "$filter"
    base="$(latest_csv)"; cp "$base" "$tmp/base.csv"

    echo ">> restoring @"
    dn jj undo
    trap - EXIT

    echo ">> diff (base @- -> new @): Mean and Allocated"
    # A tiny, dependency-free CSV diff: join on Method+Params, print Mean/Allocated deltas.
    dn python3 - "$tmp/base.csv" "$tmp/new.csv" <<'PY'
import csv, sys
def load(p):
    out={}
    with open(p, newline='') as f:
        for row in csv.DictReader(f):
            key=(row.get("Method",""), row.get("Params",""), row.get("N",""), row.get("Segments",""))
            out[key]=row
    return out
base, new = load(sys.argv[1]), load(sys.argv[2])
print(f"{'Benchmark':<48}{'Mean(base)':>16}{'Mean(new)':>16}{'Alloc(base)':>16}{'Alloc(new)':>16}")
for k in sorted(set(base)|set(new)):
    b, n = base.get(k,{}), new.get(k,{})
    name=" ".join(x for x in k if x)
    print(f"{name:<48}{b.get('Mean',''):>16}{n.get('Mean',''):>16}{b.get('Allocated',''):>16}{n.get('Allocated',''):>16}")
PY
    ;;

  *)
    grep '^#' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
