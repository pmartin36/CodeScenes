# needs_research — Live / continuous (per-keystroke) sync

**Status:** research stub, not a build milestone. v1 (M0–M11) syncs on an explicit **reconcile
gesture** (build, or capture-scene-to-code), not continuously.

## Problem
A "live" mode where edits flow between code and scene continuously and near-instantly, without an
explicit reconcile step — closer to Rojo's (experimental) two-way sync or a hot-reload feel.

## Why it's parked
- Research on Rojo is a direct caution: its live two-way sync has been "experimental / very broken"
  for years; the reliable path is the explicit, bounded `syncback`. v1 deliberately mirrors the
  reliable model.
- Continuous sync multiplies the feedback-loop, debounce, partial-edit, and conflict-timing problems
  that M7 only has to solve for discrete reconciles.

## Open questions to resolve before promotion
1. Debounce/settle strategy so mid-edit states (a half-typed builder file, an in-progress drag) don't
   trigger churn or corruption.
2. Conflict handling when both sides change between ticks — v1's "one authoritative direction per
   reconcile" (§7) has to become something finer-grained; what?
3. Editor performance under continuous snapshot diffing on large scenes.
4. Is the UX win over an explicit, fast reconcile actually worth the risk? (Possibly not.)

## Related
Builds on a rock-solid M7. Should not be attempted until discrete round-trip is proven boringly
reliable.
