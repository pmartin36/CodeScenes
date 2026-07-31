#!/usr/bin/env python3
"""Extract agent-visible friction from Unity editor logs into a durable log.

Reads Unity editor logs produced by live-verify sessions, pulls out every error
an agent hit, classifies it, and folds it into docs/agent-friction.json with
recurrence counts. Renders docs/agent-friction.md from that JSON.

Runs unattended and is incremental: each log is consumed by byte offset, so a
log that grows mid-session contributes only its new lines rather than being
re-counted from the top. A head fingerprint catches a log path REUSED by a
later session, which offset alone would misread as growth. A message already present increments its count rather
than adding a row. Safe to run on every agent stop.

Classification comes from the stack trace, not from judgement:
  Unity.Pipeline.BasePipelineServer  -> TOOLING   (Unity's CLI, not this product)
  SceneBuilder.Editor / [CodeScenes] -> PRODUCT   (this product's own output)
  everything else                    -> ENVIRONMENT
"""
import json, re, sys, hashlib, datetime
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
STATE = REPO / "docs" / "agent-friction.json"
RENDER = REPO / "docs" / "agent-friction.md"
DEFAULT_LOG_GLOBS = [
    "../Unity/SceneBuilderTest/Logs/live-verify*.log",
    "../Unity/SceneBuilderTest/Logs/Editor.log",
]

# An error line worth capturing. Unity mirrors each to a ##utp: JSON line; we
# read the plain text form and skip the mirror to avoid double counting.
ERROR_PATTERNS = [
    re.compile(r"^Command '([^']+)' failed: (.+)$"),
    re.compile(r"^ExecuteCommandByName: (.+)$"),
    re.compile(r"^(\[SceneBuilder\]|\[CodeScenes\]) (.*(?:DOES NOT COMPILE|ERROR|FAIL).*)$"),
    re.compile(r"^(Assertion failed) (.+)$"),
    re.compile(r".*(Unhandled Exception|NullReferenceException|error CS\d+).*"),
]

# Variable parts to blank so the same failure dedupes across runs.
NOISE = [
    (re.compile(r"\b\d{6,}\b"), "<id>"),
    (re.compile(r"'(/[^']*)'"), "'<path>'"),
    (re.compile(r"[A-Za-z]:?[/\\][\w./\\-]{8,}"), "<path>"),
    (re.compile(r"\b[0-9a-f]{32}\b"), "<guid>"),
    (re.compile(r"\(\d+ error\(s\)\)"), "(<n> error(s))"),
]


def classify(stack: str, message: str) -> str:
    if "Unity.Pipeline" in stack or "BasePipelineServer" in stack:
        return "TOOLING"
    if "SceneBuilder.Editor" in stack or "[SceneBuilder]" in message or "[CodeScenes]" in message:
        return "PRODUCT"
    return "ENVIRONMENT"


def normalize(message: str) -> str:
    out = message
    for pat, sub in NOISE:
        out = pat.sub(sub, out)
    return " ".join(out.split())[:400]


LOOKAHEAD = 12  # lines of stack a classification may need


def parse(path: Path, start: int, flush: bool):
    """Parse errors after byte `start`; return (entries, new_offset).

    Two ways a byte marker goes wrong while a session is still writing, both
    avoided here:

    A partial trailing line. Consuming to EOF can stop mid-line, so the next
    pass starts inside a line: the front half is parsed truncated and the back
    half as a bogus new line. Only whole lines (through the final newline) are
    ever consumed.

    Truncated context. Classification reads up to LOOKAHEAD following lines for
    the stack trace. An error near the end may not have its stack written yet,
    so those trailing lines are held back and re-read next pass WITH their
    context, rather than being consumed and misclassified. `flush` disables the
    holdback once the log has stopped growing, so a finished log's tail is not
    stranded.
    """
    try:
        raw = path.read_bytes()[start:]
    except OSError:
        return [], start
    text = raw.decode("utf-8", errors="replace")
    cut = text.rfind("\n")
    if cut < 0:
        return [], start  # no complete line yet
    complete, consumed = text[:cut + 1], start + len(text[:cut + 1].encode("utf-8"))
    lines = complete.splitlines()
    if not flush and len(lines) > LOOKAHEAD:
        held = lines[-LOOKAHEAD:]
        lines = lines[:-LOOKAHEAD]
        consumed -= len("\n".join(held).encode("utf-8")) + 1
    elif not flush:
        return [], start  # too short to classify safely; wait for more
    found = []
    for i, line in enumerate(lines):
        if line.startswith("##utp:"):
            continue  # JSON mirror of the line above
        for pat in ERROR_PATTERNS:
            if not pat.match(line):
                continue
            stack = "\n".join(lines[i + 1 : i + 12])
            verbatim = line.strip()
            # A DOES NOT COMPILE report carries its detail on following lines.
            if "DOES NOT COMPILE" in verbatim:
                detail = [l for l in lines[i + 1 : i + 6] if "CS" in l]
                if detail:
                    verbatim = f"{verbatim} | {detail[0].strip()}"
            found.append((normalize(verbatim), verbatim, classify(stack, verbatim)))
            break
    return found, consumed


def main(argv):
    today = datetime.date.today().isoformat()
    state = json.loads(STATE.read_text()) if STATE.exists() else {"seen_logs": {}, "entries": {}}

    logs = [Path(a) for a in argv[1:]] or [
        p for g in DEFAULT_LOG_GLOBS for p in sorted(REPO.glob(g))
    ]

    folded = 0
    for log in logs:
        if not log.exists():
            continue
        key = str(log.resolve())
        size = log.stat().st_size
        # A log path can be REUSED by a later session, which truncates and starts
        # over. Offset alone cannot tell that from growth, and guessing "it grew"
        # would silently skip the new session's opening lines. Fingerprint the
        # head: if it changed, this is a different log wearing the same name.
        with log.open("rb") as fh:
            head = hashlib.sha256(fh.read(512)).hexdigest()[:16]
        prev = state["seen_logs"].get(key)
        consumed = prev.get("bytes", 0) if isinstance(prev, dict) else 0
        if not isinstance(prev, dict) or prev.get("head") != head:
            consumed = 0  # new file, or migrating from an older state format
        if consumed > size:
            consumed = 0  # truncated
        grew = not isinstance(prev, dict) or prev.get("size") != size
        if consumed == size and not grew:
            continue  # nothing new, and the tail is already flushed
        entries, consumed = parse(log, consumed, flush=not grew)
        if not entries and consumed == (prev or {}).get("bytes", -1):
            state["seen_logs"][key] = {"bytes": consumed, "head": head, "size": size}
            continue
        for norm, verbatim, cls in entries:
            e = state["entries"].setdefault(
                norm,
                {"count": 0, "class": cls, "verbatim": verbatim,
                 "first_seen": today, "last_seen": today, "source": str(log)},
            )
            e["count"] += 1
            e["last_seen"] = today
            e["class"] = cls
        state["seen_logs"][key] = {"bytes": consumed, "head": head, "size": size}
        folded += 1

    if not folded:
        return 0

    STATE.parent.mkdir(parents=True, exist_ok=True)
    STATE.write_text(json.dumps(state, indent=2, sort_keys=True) + "\n")

    everything = list(state["entries"].values())
    rows = sorted([e for e in everything if e.get("status", "open") == "open"],
                  key=lambda e: (-e["count"], e["class"]))
    done = sorted([e for e in everything if e.get("status", "open") != "open"],
                  key=lambda e: -e["count"])
    out = [
        "# Agent friction log",
        "",
        "Errors agents hit while driving this product and its tooling, extracted mechanically from",
        "Unity editor logs. Generated by `tools/extract-agent-friction.py`; do not hand-edit — edit",
        "the script or `agent-friction.json`.",
        "",
        "`PRODUCT` entries are this product's own output and are candidates for a spec item once they",
        "recur. `TOOLING` is Unity's Pipeline CLI. `ENVIRONMENT` is the editor/host.",
        "",
        "| Count | Class | First | Last | Message |",
        "|---|---|---|---|---|",
    ]
    for e in rows:
        msg = e["verbatim"].replace("|", "\\|")[:220]
        out.append(f"| {e['count']} | {e['class']} | {e['first_seen']} | {e['last_seen']} | {msg} |")
    if done:
        out += ["", "## Promoted or resolved", "",
                "Kept so a recurrence is visible as a regression rather than looking new.",
                "Set `status` to `promoted` or `wontfix` in `agent-friction.json` to move a row here.",
                "", "| Count | Class | Status | Message |", "|---|---|---|---|"]
        for e in done:
            msg = e["verbatim"].replace("|", "\\|")[:180]
            out.append(f"| {e['count']} | {e['class']} | {e.get('status')} | {msg} |")
    out.append("")
    RENDER.write_text("\n".join(out))
    print(f"agent-friction: folded {folded} log(s), {len(rows)} distinct entries")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
