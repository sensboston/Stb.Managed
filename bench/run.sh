#!/usr/bin/env bash
# Builds both harnesses, runs the benchmark passes and archives the results.
# Usage: run.sh [--corpus DIR] [--cases FILE] [--filter TEXT] [--min-time-ms N] [--framework TFM] [--skip-gpu] [--managed-exe FILE]
set -u -o pipefail

BENCH_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$BENCH_ROOT")"

CORPUS="bench/corpus"
CASES="bench/cases.tsv"
FILTER=""
MIN_TIME_MS="1000"
FRAMEWORK="net10.0"
SKIP_GPU=0
MANAGED_EXE=""

usage() {
    echo "Usage: run.sh [--corpus DIR] [--cases FILE] [--filter TEXT] [--min-time-ms N] [--framework TFM] [--skip-gpu] [--managed-exe FILE]"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --corpus) CORPUS="$2"; shift 2 ;;
        --corpus=*) CORPUS="${1#*=}"; shift ;;
        --cases) CASES="$2"; shift 2 ;;
        --cases=*) CASES="${1#*=}"; shift ;;
        --filter) FILTER="$2"; shift 2 ;;
        --filter=*) FILTER="${1#*=}"; shift ;;
        --min-time-ms) MIN_TIME_MS="$2"; shift 2 ;;
        --min-time-ms=*) MIN_TIME_MS="${1#*=}"; shift ;;
        --framework) FRAMEWORK="$2"; shift 2 ;;
        --framework=*) FRAMEWORK="${1#*=}"; shift ;;
        --skip-gpu) SKIP_GPU=1; shift ;;
        --managed-exe) MANAGED_EXE="$2"; shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

join_under() {
    case "$2" in /*) printf '%s\n' "$2" ;; *) printf '%s\n' "$1/$2" ;; esac
}

CORPUS_PATH="$(join_under "$REPO_ROOT" "$CORPUS")"
CASES_PATH="$(join_under "$REPO_ROOT" "$CASES")"
NATIVE_DIR="$BENCH_ROOT/native"
NATIVE_BUILD="$NATIVE_DIR/build"
MANAGED_PROJECT="$BENCH_ROOT/Stb.Managed.Bench"
RESULTS_ROOT="$BENCH_ROOT/results"

if [ ! -d "$CORPUS_PATH" ]; then echo "Corpus directory not found: $CORPUS_PATH" >&2; exit 2; fi
if [ ! -f "$CASES_PATH" ]; then echo "Cases file not found: $CASES_PATH" >&2; exit 2; fi
command -v cmake >/dev/null 2>&1 || { echo "cmake not found in PATH" >&2; exit 2; }
[ -n "$MANAGED_EXE" ] || command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found in PATH" >&2; exit 2; }

MACHINE="$(hostname 2>/dev/null || uname -n)"
MACHINE="$(printf '%s' "$MACHINE" | tr -c 'A-Za-z0-9._-' '_')"
RESULTS_DIR="$RESULTS_ROOT/$MACHINE"
rm -rf "$RESULTS_DIR"
mkdir -p "$RESULTS_DIR"

NATIVE_CSV="$RESULTS_DIR/native.csv"
MANAGED_1T="$RESULTS_DIR/managed_1t.csv"
MANAGED_0T="$RESULTS_DIR/managed_0t.csv"
MANAGED_DEFAULT="$RESULTS_DIR/managed_default.csv"
SYSINFO="$RESULTS_DIR/sysinfo.json"

echo "Corpus:    $CORPUS_PATH"
echo "Cases:     $CASES_PATH"
echo "Framework: $FRAMEWORK"
echo "Results:   $RESULTS_DIR"
echo

CMAKE_ARGS=(-S "$NATIVE_DIR" -B "$NATIVE_BUILD" -DCMAKE_BUILD_TYPE=Release)
if command -v clang >/dev/null 2>&1 && command -v ninja >/dev/null 2>&1; then
    echo "Using Ninja + clang"
    CMAKE_ARGS+=(-G Ninja -DCMAKE_C_COMPILER=clang)
fi

echo "=== native build ==="
cmake "${CMAKE_ARGS[@]}" || exit 1
cmake --build "$NATIVE_BUILD" --config Release --parallel || exit 1

OUT_DIR="$NATIVE_BUILD/out"
NATIVE_EXE=""
for candidate in "$OUT_DIR/stb_bench_native" "$OUT_DIR/stb_bench_native.exe" "$OUT_DIR/Release/stb_bench_native" "$OUT_DIR/Release/stb_bench_native.exe"; do
    if [ -f "$candidate" ]; then NATIVE_EXE="$candidate"; break; fi
done
if [ -z "$NATIVE_EXE" ]; then echo "Native benchmark binary not found under $OUT_DIR" >&2; exit 1; fi
echo "Native binary: $NATIVE_EXE"
echo

if [ -z "$MANAGED_EXE" ]; then
    echo "=== managed build ==="
    dotnet build "$MANAGED_PROJECT" -c Release --framework "$FRAMEWORK" || exit 1
    MANAGED_EXE="$MANAGED_PROJECT/bin/Release/$FRAMEWORK/Stb.Managed.Bench.dll"
fi
MANAGED_DLL="$MANAGED_EXE"
if [ ! -f "$MANAGED_DLL" ]; then echo "Managed binary not found: $MANAGED_DLL" >&2; exit 1; fi
managed() { case "$MANAGED_DLL" in *.dll) dotnet "$MANAGED_DLL" "$@" ;; *) "$MANAGED_DLL" "$@" ;; esac; }
echo

FAILURES=0
note_failure() { FAILURES=$((FAILURES + 1)); echo "WARNING: $1" >&2; }

print_summary() {
    echo
    echo "=== $1 ==="
    native_csv="$2"
    managed_csv="$3"
    if [ ! -f "$native_csv" ] || [ ! -f "$managed_csv" ]; then
        echo "(missing CSV, skipped)"
        return
    fi
    LC_ALL=C awk -F',' '
        function unquote(s) { gsub(/^"|"$/, "", s); return s }
        FILENAME == ARGV[1] { src = 1 }
        FILENAME == ARGV[2] { src = 2 }
        /^#/ { next }
        /^case,op,/ { next }
        {
            c = unquote($1)
            if (src == 1) { nat_med[c] = $5; nat_chk[c] = $7; nat_st[c] = $8 }
            else { order[++n] = c; man_med[c] = $5; man_chk[c] = $7; man_st[c] = $8 }
        }
        END {
            printf "%-36s %12s %12s %9s %-8s %s\n", "case", "native_ms", "managed_ms", "speedup", "checksum", "note"
            for (i = 1; i <= n; i++) {
                c = order[i]
                nmed = (c in nat_med) ? nat_med[c] : ""
                nst = (c in nat_st) ? nat_st[c] : "missing"
                mst = man_st[c]
                speedup = "n/a"
                if (nst == "ok" && mst == "ok" && nmed + 0 > 0 && man_med[c] + 0 > 0)
                    speedup = sprintf("%.2fx", nmed / man_med[c])
                chk = "n/a"
                if ((c in nat_chk) && nat_chk[c] != "" && man_chk[c] != "")
                    chk = (nat_chk[c] == man_chk[c]) ? "match" : "DIFFER"
                note = ""
                if (nst != "ok" || mst != "ok") note = "native=" nst " managed=" mst
                printf "%-36s %12s %12s %9s %-8s %s\n", c, nmed, man_med[c], speedup, chk, note
            }
        }
    ' "$native_csv" "$managed_csv"
}

COMMON=(--corpus "$CORPUS_PATH" --cases "$CASES_PATH" --min-time-ms "$MIN_TIME_MS")
if [ -n "$FILTER" ]; then COMMON+=(--filter "$FILTER"); fi

echo "=== native run ==="
"$NATIVE_EXE" "${COMMON[@]}" --out "$NATIVE_CSV" || note_failure "native run reported failures"

echo "=== managed run (threads=1) ==="
managed "${COMMON[@]}" --out "$MANAGED_1T" --threads 1 || note_failure "managed threads=1 run reported failures"

echo "=== managed run (threads=0) ==="
managed "${COMMON[@]}" --out "$MANAGED_0T" --threads 0 || note_failure "managed threads=0 run reported failures"

managed --sysinfo "$SYSINFO" || note_failure "sysinfo failed"
if [ "$SKIP_GPU" = 1 ]; then export STB_GPU_MODE=cpu; echo "GPU disabled by --skip-gpu"; fi
echo "=== managed run (default: all cores, GPU auto) ==="
managed "${COMMON[@]}" --out "$MANAGED_DEFAULT" --default || note_failure "managed default run reported failures"
print_summary "summary: native vs managed threads=1" "$NATIVE_CSV" "$MANAGED_1T"
print_summary "summary: native vs managed default" "$NATIVE_CSV" "$MANAGED_DEFAULT"

echo
ZIP_PATH="$RESULTS_ROOT/$MACHINE.zip"
rm -f "$ZIP_PATH"
if command -v zip >/dev/null 2>&1; then
    (cd "$RESULTS_ROOT" && zip -qr "$MACHINE.zip" "$MACHINE")
else
    echo "WARNING: zip not available, archive skipped" >&2
fi
echo "Results: $RESULTS_DIR"
if [ -f "$ZIP_PATH" ]; then echo "Archive: $ZIP_PATH"; fi

if [ "$FAILURES" -gt 0 ]; then exit 1; fi
exit 0
