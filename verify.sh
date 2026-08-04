#!/usr/bin/env bash
# CodeScenes verification gate. THIS is the pipeline's gate command.
#
#   Layer 1 (always): the fast headless Core suite — dotnet build + test. Seconds.
#   Layer 2 (conditional): the Unity EditMode suite — real editor behavior. Minutes.
#     Runs when the working change set touches Unity-facing code (com.codescenes/ or
#     unity-gate/), when the tree is clean (nothing in flight to prove otherwise), or when
#     forced (GATE_FORCE_UNITY=1). GATE_SKIP_UNITY=1 runs Core only and says so.
#
# A Unity-touching change CANNOT pass without a green Unity result: a missing/failed
# results.xml is a FAILURE, never "probably fine". A pure-Core change skips Layer 2 and
# SAYS SO — a skip never masquerades as a Unity pass.
#
# Two cheap checks wrap both layers: a lint rejecting pipeline-artifact prose in comments the
# working diff ADDS, and a cleanliness delta rejecting residue the gate run itself leaves behind.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO"
export PATH="$HOME/.dotnet:$PATH"

# ---- Auto-staged build artifacts ----
# The full set of git-tracked files that a plain `dotnet build SceneBuilder.sln` rewrites, via the
# post-build Copy targets in SceneBuilder.Core.csproj, SceneBuilder.Grammar.csproj and
# CodeScenes.Analyzers.csproj. They are BUILD ARTIFACTS of a Core/Grammar/Analyzer change, never
# source changes, so they can never imply Unity-facing work and they are never evidence that a
# gate run left the tree dirty. Two checks below key off this list.
BUILD_ARTIFACTS_RE='^com\.codescenes/(Plugins/(SceneBuilder\.Core|SceneBuilder\.Grammar)\.dll|Analyzers~/(SceneBuilder\.Grammar|CodeScenes\.Analyzers)\.dll)$'

# ---- Decide whether Layer 2 is required ----
# This MUST be computed BEFORE `dotnet build` below. The post-build targets restage git-tracked
# DLLs — so building first makes the gate's own build dirty a path matching the trigger, and EVERY
# Core change then drags in the multi-minute editor suite.
#
# The staged DLLs are also excluded from the trigger outright. The exclusion is what holds when the
# ordering alone is not enough — a DLL left dirty by an earlier build. Both guards are required.
#
# The change set is the WORKING TREE against HEAD, plus untracked files. Untracked files count
# because `git diff --name-only` never lists them, so a change consisting only of NEW Unity files
# (a new MonoBehaviour + its EditMode test) would otherwise leave the trigger cold and skip the
# suite that is the change's whole point. Adding a NEW plugin still triggers Layer 2 via its .meta,
# which is not excluded. Committed history is deliberately NOT consulted: once a run's first adapter
# change committed, a HEAD~1..HEAD term kept the trigger hot for every later Core-only change.
#
# A change set that is EMPTY after exclusions runs Layer 2. Nothing is in flight to prove the run is
# Core-only, so the run is a full verification of committed state — this is what the pipeline's
# post-commit final pass runs.
#
# Env overrides, force wins over skip:
#   GATE_FORCE_UNITY=1  run Layer 2 whatever the change set says.
#   GATE_SKIP_UNITY=1   Core only; the verdict line says so and is not a Unity pass. This is the
#                       pipeline's per-task fast gate for tasks whose TOUCHES miss the adapter.
changed="$(git diff --name-only HEAD; git ls-files --others --exclude-standard)"
relevant="$(echo "$changed" | grep -vE "$BUILD_ARTIFACTS_RE" | grep -vE '^\s*$')"
need_unity=0
unity_reason=""
if [[ -z "$relevant" ]]; then
  need_unity=1
  unity_reason="no source changes in flight; verifying committed state in full"
else
  echo "$relevant" | grep -qE '^(com\.codescenes|unity-gate)/' && {
    need_unity=1
    unity_reason="change set touches com.codescenes/ or unity-gate/"
  }
fi
if [[ "${GATE_SKIP_UNITY:-0}" == "1" ]]; then
  need_unity=0
  skip_reason="GATE_SKIP_UNITY=1 requested it; this is NOT a Unity pass"
else
  skip_reason="no com.codescenes/ or unity-gate/ changes"
fi
[[ "${GATE_FORCE_UNITY:-0}" == "1" ]] && { need_unity=1; unity_reason="forced by GATE_FORCE_UNITY=1"; }

# GATE_TRIGGER_ONLY=1 prints the Layer 2 decision and exits without running either layer. It is how
# the trigger itself is checked; it is NOT a gate run and never prints a verdict line.
if [[ "${GATE_TRIGGER_ONLY:-0}" == "1" ]]; then
  echo "TRIGGER: unity=$need_unity reason=$([[ "$need_unity" -eq 1 ]] && echo "$unity_reason" || echo "$skip_reason")"
  exit 0
fi

# ---- Lint: pipeline-artifact prose in ADDED source lines ----
# A shipped code comment may describe the file's own CURRENT contract, in present tense. It may
# never describe pipeline process: another task's id, a handoff artifact, an iteration number, or
# pending/hand-off state that goes false the moment the work lands.
#
# Scoped to lines the WORKING DIFF adds (tracked diff vs HEAD + every untracked .cs), because a
# repo-wide version would trip over hundreds of pre-existing hits from earlier features and be
# unshippable. Diff-scoping is what makes it a gate every current and future writer inherits by
# default, instead of a grep each blueprint re-derives and each task hand-runs.
PROSE_AWK='
function emit(f, n, txt,   c, i, t) {
  # Only the COMMENT part of a line is prose. Code — identifiers, and string literals a test
  # asserts on — is never matched.
  c = ""
  i = index(txt, "//")
  if (i > 0) c = substr(txt, i)
  else { t = txt; sub(/^[ \t]+/, "", t); if (t ~ /^\*/ || t ~ /^\/\*/) c = t }
  if (c == "") return
  if (c ~ /b[0-9]-t[0-9]/ ||
      c ~ /research\.md|validator\.md|test-writer\.md|code-writer\.md|tasks\.md|state\.md|scope\/bucket-/ ||
      c ~ /[Ii]teration [0-9]/ ||
      c ~ /RED today|red today|not yet built|downstream task|this task|pending task/) {
    printf "%s:%d: %s\n", f, n, c
  }
}
/^\+\+\+ / { file = substr($0, 7); next }
/^--- /    { next }
/^@@ /     { split($0, a, "+"); split(a[2], b, /[, ]/); lineno = b[1] + 0; next }
/^\+/      { emit(file, lineno, substr($0, 2)); lineno++; next }
'
prose_hits="$(
  {
    git diff -U0 HEAD -- '*.cs'
    git ls-files --others --exclude-standard -- '*.cs' | while IFS= read -r f; do
      printf '+++ b/%s\n@@ -0,0 +1 @@\n' "$f"
      sed 's/^/+/' "$f"
    done
  } | awk "$PROSE_AWK" | grep -E '^(SceneBuilder\.Core|com\.codescenes/|unity-gate/Assets/)'
)"
if [[ -n "$prose_hits" ]]; then
  echo "== Pipeline-artifact prose in added comments =="
  echo "$prose_hits" | sed 's/^/  /'
  echo "A code comment describes the file's own CURRENT contract in present tense — never a task id,"
  echo "a handoff artifact (research.md / validator.md / test-writer.md / tasks.md / state.md /"
  echo "scope/bucket-*), an iteration number, or pending/handoff state."
  echo "GATE FAIL: pipeline-artifact prose in added comments"
  exit 1
fi

# ---- Cleanliness baseline (compared again after the run) ----
# Captured BEFORE anything builds, and compared BY DELTA: only files this gate run itself moves
# from clean to dirty are reported. An absolute "the tree must be clean" check would fire on any
# legitimately in-flight working tree and make a paused/resumed pipeline impossible.
dirty_tracked() { git status --porcelain --untracked-files=no | awk '{print $NF}' | grep -vE "$BUILD_ARTIFACTS_RE" | sort; }
DIRTY_BEFORE="$(dirty_tracked)"

# Fails the gate when the run leaves residue behind: a tracked file it dirtied on its own, or a
# temp EditMode scene asset an author forgot to delete in [TearDown] (which then dirties the
# tracked asset catalog on the next regeneration). The check belongs in the gate every test
# inherits by default, not in each test's TearDown.
check_clean_after_run() {
  local leftovers newly_dirty rc=0
  leftovers="$(ls -1 unity-gate/Assets/GateTests/__*.unity 2>/dev/null)"
  if [[ -n "$leftovers" ]]; then
    echo "== Gate run left temp EditMode scene assets behind =="
    echo "$leftovers" | sed 's/^/  /'
    echo "  Each EditMode test that saves a temp scene must delete it in [TearDown]."
    rc=1
  fi
  newly_dirty="$(comm -13 <(echo "$DIRTY_BEFORE") <(dirty_tracked))"
  if [[ -n "$newly_dirty" ]]; then
    echo "== Gate run dirtied tracked files that were clean before it started =="
    echo "$newly_dirty" | sed 's/^/  /'
    echo "  (build artifacts are excluded; these are real writes the run performed)"
    rc=1
  fi
  return $rc
}

# ---- Layer 1: Core (always) ----
echo "== Core gate: dotnet build + test =="
if ! dotnet build SceneBuilder.sln; then echo "GATE FAIL: dotnet build"; exit 1; fi
if ! dotnet test  SceneBuilder.sln; then echo "GATE FAIL: dotnet test";  exit 1; fi

if [[ "$need_unity" -eq 0 ]]; then
  if ! check_clean_after_run; then echo "GATE FAIL: the gate run left the tree dirty"; exit 1; fi
  echo "GATE PASS: Core green (Unity EditMode gate skipped: $skip_reason)"
  exit 0
fi

# ---- Layer 2: Unity EditMode (real editor behavior) ----
echo "== Unity EditMode gate: real editor behavior ($unity_reason) =="
UNITY="${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/6000.5.3f1/Editor/Unity}"
GATE="$REPO/unity-gate"
RESULTS="$GATE/results.xml"
if [[ ! -x "$UNITY" ]]; then echo "GATE FAIL: Unity editor not found at $UNITY (set UNITY_EDITOR)"; exit 1; fi

rm -f "$RESULTS"
# Do NOT pkill Unity.Licensing.Client here. That daemon is SHARED per-user across every Unity
# instance, so killing it yanks the license out from under an interactive editor mid-session
# ("The connection with the Unity Licensing Client has been lost") and races a replacement daemon
# into existence, producing 505 "Unsupported protocol version" handshake rejections. Batchmode
# needs no such cleanup: the first-attempt `LicenseClient-<user>` handshake warning self-recovers
# on the versioned `LicenseClient-<user>-<version>` channel.
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

if ! check_clean_after_run; then echo "GATE FAIL: the gate run left the tree dirty"; exit 1; fi

echo "GATE PASS: Core + Unity EditMode green (passed=$passed failed=$failed skipped=${skipped:-0})"
exit 0
