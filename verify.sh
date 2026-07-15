#!/usr/bin/env bash
# SceneBuilder verification gate. THIS is the pipeline's gate command.
#
#   Layer 1 (always): the fast headless Core suite — dotnet build + test. Seconds.
#   Layer 2 (conditional): the Unity EditMode suite — real editor behavior. Minutes.
#     Runs ONLY when the change touches Unity-facing code (com.scenebuilder/ or unity-gate/),
#     or when forced (GATE_FORCE_UNITY=1 — used by the pipeline's final cross-bucket pass).
#
# A Unity-touching change CANNOT pass without a green Unity result: a missing/failed
# results.xml is a FAILURE, never "probably fine". A pure-Core change skips Layer 2 and
# SAYS SO — a skip never masquerades as a Unity pass.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO"
export PATH="$HOME/.dotnet:$PATH"

# ---- Layer 1: Core (always) ----
echo "== Core gate: dotnet build + test =="
if ! dotnet build SceneBuilder.sln; then echo "GATE FAIL: dotnet build"; exit 1; fi
if ! dotnet test  SceneBuilder.sln; then echo "GATE FAIL: dotnet test";  exit 1; fi

# ---- Decide whether Layer 2 is required ----
changed="$(git diff --name-only HEAD; git diff --name-only HEAD~1 HEAD 2>/dev/null)"
need_unity=0
[[ "${GATE_FORCE_UNITY:-0}" == "1" ]] && need_unity=1
echo "$changed" | grep -qE '^(com\.scenebuilder|unity-gate)/' && need_unity=1

if [[ "$need_unity" -eq 0 ]]; then
  echo "GATE PASS: Core green (Unity EditMode gate skipped — no com.scenebuilder/ or unity-gate/ changes)"
  exit 0
fi

# ---- Layer 2: Unity EditMode (real editor behavior) ----
echo "== Unity EditMode gate: real editor behavior =="
UNITY="${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/6000.5.3f1/Editor/Unity}"
GATE="$REPO/unity-gate"
RESULTS="$GATE/results.xml"
if [[ ! -x "$UNITY" ]]; then echo "GATE FAIL: Unity editor not found at $UNITY (set UNITY_EDITOR)"; exit 1; fi

rm -f "$RESULTS"
pkill -f 'Unity.Licensing.Client' 2>/dev/null || true   # clear a stale licensing daemon
timeout 1200 "$UNITY" -runTests -batchmode -nographics \
  -projectPath "$GATE" -testPlatform EditMode \
  -testResults "$RESULTS" -logFile "$GATE/editor.log"
ucode=$?

# Gate on BOTH exit code AND the results XML (6000.x exit codes are occasionally unreliable).
if [[ ! -s "$RESULTS" ]]; then
  echo "GATE FAIL: no Unity results.xml (compile / license / startup failure), unity_exit=$ucode"
  echo "---- last 30 log lines ----"; tail -30 "$GATE/editor.log" 2>/dev/null
  exit 1
fi
run_line="$(grep -oE '<test-run[^>]*>' "$RESULTS" | head -1)"
result="$(echo "$run_line" | grep -oE 'result="[^"]*"' | head -1 | cut -d'"' -f2)"
failed="$(echo "$run_line" | grep -oE 'failed="[^"]*"' | head -1 | cut -d'"' -f2)"
passed="$(echo "$run_line" | grep -oE 'passed="[^"]*"' | head -1 | cut -d'"' -f2)"
skipped="$(echo "$run_line" | grep -oE 'skipped="[^"]*"' | head -1 | cut -d'"' -f2)"

# STRICT: the run-level result must be "Passed" AND failed must be 0 AND the XML must exist.
# NUnit downgrades a whole run to "Skipped:Ignored" the moment ANY test is ignored, so requiring
# result="Passed" is exactly what makes an ignored/quarantined test unable to pass this gate in
# silence. That is the point: a skip is not a pass, and a green gate must mean every test RAN.
# Do NOT relax this to `failed=0` alone to accommodate a quarantine — quarantine loudly in the test
# file instead, and let the gate stay red until the bug is fixed.
if [[ "$ucode" -ne 0 || "$result" != "Passed" || "${failed:-1}" != "0" ]]; then
  echo "GATE FAIL: Unity EditMode red (unity_exit=$ucode result=$result failed=$failed skipped=${skipped:-0})"
  if [[ "${skipped:-0}" != "0" ]]; then
    echo "---- $skipped SKIPPED/IGNORED test(s) — these are NOT passes: ----"
    grep -oE '<test-case[^>]*result="Skipped"[^>]*>' "$RESULTS" \
      | grep -oE 'fullname="[^"]*"' | cut -d'"' -f2 | sed 's/^/     - /'
    echo "   (reasons are in the <reason> nodes of $RESULTS)"
  fi
  exit 1
fi

echo "GATE PASS: Core + Unity EditMode green (passed=$passed failed=$failed skipped=${skipped:-0})"
exit 0
