# M7 — Robustness pass (hardening the loop)

### Additions to the contract

No new §3 data-model types. This milestone hardens existing behavior and adds two persisted-state /
adapter concepts that are implementation surface, not model types:

- **`SyncCheckpoint`** — the plugin's on-disk resync state (not a `SceneModel` type). Persisted next
  to the sidecar so the loop survives domain reloads and process exit:
  ```
  FooScene.sbstate.json
    SchemaVersion
    Scene            : "Assets/Scenes/Foo.unity"
    LastSnapshotHash : string     // canonical hash of the last reconciled SceneSnapshot
    LastSourceHash   : string     // canonical hash of the last built/parsed SceneModel
    LastSidecarHash  : string     // hash of the IdentityMap at last checkpoint
  ```
- **`SuppressionScope`** — an Editor-side guard (a ref-counted flag), not a Core type. Bounds the
  window during which `ObjectChangeEvents` are treated as self-inflicted and ignored.

Everything else uses foundation types verbatim: `SceneSnapshot`, `SceneModel`, `Plan`, `SourcePatch`,
`IdentityMap`, the canonical serializer, differ, Materialize, Reconcile (§3–§5).

---

## Goal

Make the bidirectional loop trustworthy under real Unity conditions: our own writes don't echo back,
script recompiles/domain reloads don't lose sync, edits made while Unity wasn't watching are
recovered, and repeated cycles produce byte-stable output with zero drift or id churn.

## In scope

Five independently testable hardening behaviors:

- **(a) Self-triggered event suppression** — a code→scene Materialize must not re-trigger a scene→code
  Reconcile via `ObjectChangeEvents`. Suppress/guard the event stream for the duration of Plan
  execution.
- **(b) Domain-reload survival** — re-subscribe to events via `[InitializeOnLoad]`; persist plugin
  state to disk (not memory); treat every reload as a **resync-from-disk checkpoint** (§5:
  "Reconcile on every domain reload and focus-regain").
- **(c) External-edit reconciliation** — a scene changed on disk by git/another tool while unobserved
  is recovered by a **full-snapshot diff** on next focus/reload, even though no `ObjectChangeEvent`
  fired (the authority is a fresh snapshot, per §5).
- **(d) Canonical determinism hardening** — repeated build/reconcile cycles produce byte-stable source
  and sidecar: no reordering churn, no float-format drift, no spurious diffs.
- **(e) Repeated round-trip stability** — N cycles of edit-in-Unity ↔ patch-source converge with no
  drift, no LogicalId/GlobalObjectId churn.

## Out of scope

- New authored capabilities or model types (this M adds no user-facing authoring surface).
- Live per-keystroke sync (parked in `needs_research`).
- Multi/additive scene handling (parked in `needs_research`).
- Merge/conflict *resolution* UX beyond surfacing (§7 conflict philosophy already owns surfacing).
- Performance/scale tuning beyond "no spurious work"; correctness of stability is the target, not
  throughput.

## Core deliverables

**Types added/changed (referencing §3):** none to the data model. Add `SyncCheckpoint` persistence
(read/write, hashing) alongside the IdentityMap I/O; hashing uses the existing canonical serializer.

**Functions/behaviors (each a testable contract):**
- **Deterministic canonical output (d):** `Canonicalize(model)` is a pure function of the model —
  same `SceneModel` ⇒ byte-identical text across runs, processes, and cultures. Component/child/field
  ordering, float formatting (round-trippable invariant-culture form), and quaternion emission are
  fixed and stable.
- **Snapshot-diff recovery (c):** `Reconcile(source, freshSnapshot, map)` recovers a change that
  produced **no event** — given a snapshot differing from source, it yields the same `SourcePatch` it
  would have from an event-triggered diff. Events are trigger-only; the diff is authority (§5).
- **Round-trip idempotence (e):** applying a `SourcePatch`, re-parsing to `SceneModel`, and
  re-Materializing against the same snapshot yields an **empty Plan** (no-op). Symmetrically, a
  desired==actual Materialize yields an empty Plan and no SourcePatch.
- **No id churn (e):** across N reconcile/materialize cycles on unchanged structure, every
  `LogicalId`, `GlobalObjectId`, and IdentityMap entry is byte-identical to cycle 1 — synthesized ids
  are read from the map, never re-derived to a new value (§4).
- **Checkpoint compare (b):** `SyncCheckpoint` hashes let the plugin decide, on reload, whether source
  / snapshot / sidecar changed while it was down, and route to the correct direction or surface a
  conflict (§7) instead of guessing.
- **Suppression correctness (a):** Core exposes the reconcile entry point as *pure* (snapshot in,
  patch out) so suppression is purely an adapter-timing concern; a Reconcile invoked with the
  post-Materialize snapshot equal to desired yields an empty patch (defense in depth even if a stray
  event leaks).

## Editor adapter deliverables

- **Suppression flag (a):** a ref-counted `SuppressionScope`; the executor enters it before Plan
  execution and exits after `sceneSaved`/settle, so `ObjectChangeEvents.changesPublished` fired by our
  own writes are dropped. The scope is exception-safe (always exits) and re-entrant.
- **`[InitializeOnLoad]` re-init (b):** on every domain reload, re-subscribe
  `ObjectChangeEvents.changesPublished` + `sceneSaved`, load `SyncCheckpoint` + IdentityMap from disk,
  and run a **resync**: read a fresh `SceneSnapshot`, compare against checkpoint hashes, Reconcile if
  the scene moved out from under us.
- **Focus/reload resync hook (b,c):** on `EditorApplication` focus-regain and on scene-open, run the
  same full-snapshot resync (do not trust the event stream to have been complete).
- **State to disk, not memory (b):** all cross-reload state (`SyncCheckpoint`, IdentityMap) lives in
  files; nothing sync-critical is held only in static fields that a reload would clear.

## Authoring API added

None. M7 is invisible to authored source; existing `Build(SceneRoot scene)` files are unchanged.

## IdentityMap / sidecar changes

- No schema change to `FooScene.sbmap.json`.
- Add the sibling **`FooScene.sbstate.json`** (`SyncCheckpoint`, see additions) written atomically at
  each checkpoint (after a successful Materialize and after a successful Reconcile).
- IdentityMap writes become **idempotent**: re-writing an unchanged map produces a byte-identical file
  (stable entry ordering, no timestamp/nondeterministic fields), so (d) holds for the sidecar too.

## Core test plan

RED tests `tdd-pipeline` will write (behaviors, headless, no Unity):

1. **Determinism — same input, byte-identical output:** canonicalize the same `SceneModel` twice (and
   under a non-invariant culture, e.g. `de-DE`) ⇒ byte-identical text; sidecar re-write ⇒ byte-identical
   file.
2. **Diff-on-snapshot recovers a no-event change:** construct a snapshot that differs from source with
   **no** change event supplied ⇒ Reconcile produces the correct `SourcePatch`; assert it equals the
   patch produced when the same delta arrives via an event trigger.
3. **Round-trip idempotence:** apply a `SourcePatch` → re-parse → re-Materialize against the same
   snapshot ⇒ **empty Plan**; desired==actual Materialize ⇒ empty Plan and empty SourcePatch.
4. **N-cycle stability / no drift:** run 10 alternating Reconcile/Materialize cycles on unchanged
   structure ⇒ source text, sidecar bytes, and every `LogicalId`/`GlobalObjectId` are identical to
   cycle 1 (no churn).
5. **Checkpoint routing:** given `SyncCheckpoint` hashes plus a fresh snapshot/source/sidecar, the
   direction decision is correct for each case (only-scene-changed ⇒ Reconcile; only-source-changed ⇒
   Materialize; both-changed ⇒ conflict surfaced, never last-write-wins per §7).
6. **Float/ordering hardening:** models differing only by input author-order or float representation
   (e.g. `5f` vs `5.0`) canonicalize to identical text ⇒ no spurious diff.
7. **Suppression defense-in-depth:** Reconcile called with the post-Materialize snapshot (== desired)
   ⇒ empty patch (even if an event leaked through suppression).

## Unity confirmation checklist

Steps the user performs on a real Unity 6 project; expected result each step:

1. **Self-event suppression (a):** build a scene from code (Materialize). Watch the sync log →
   **no** scene→code Reconcile fires as a result of our own writes; the source file is not re-patched.
2. **External edit then refocus (c):** with Unity unfocused, edit the `.unity` file via git
   (e.g. `git checkout` a variant / a text edit that moves an object), return focus to Unity →
   the full-snapshot resync detects the change and Reconciles the source (event stream was empty).
3. **Recompile mid-session (b):** trigger a script recompile / domain reload during a session →
   events re-subscribe via `[InitializeOnLoad]`, state reloads from disk, and a resync runs; sync
   still works on the next edit with no lost subscription.
4. **10× round-trip (d,e):** pick one object; move/rename it in Unity ↔ let it patch source, repeat 10
   times → **no corruption, no diff noise**: after settling, git diff of source + sidecar is clean
   beyond the intended change, and the object's LogicalId/GlobalObjectId are unchanged throughout.
5. **Determinism (d):** build the same scene twice from a clean state → `git diff` on the generated
   source and sidecar is **empty** (byte-stable).

## Dependencies

- **M0** — build trigger, Plan seam, sidecar I/O.
- **M2** — Reconcile + SourcePatch (the loop this milestone hardens).
- **All of M1–M6** — the loop must stay stable across every capability shipped so far (transforms,
  components, assets, references, prefab instances).

## Risks/notes

- Suppression must be **time-bounded and exception-safe**: a scope that fails to exit would deafen the
  plugin to all future scene edits — always release in a `finally`, and treat a still-open scope after
  settle as a bug to surface, not to leave latched.
- Over-suppression can swallow a legitimate concurrent user edit made during Materialize; the
  focus/reload full-snapshot resync (c) is the backstop that recovers any edit suppression missed —
  this is why the diff, not the event, is authority (§5).
- Determinism killers to guard against: culture-sensitive float formatting, dictionary/hash-set
  iteration order, timestamps or machine paths in the sidecar, and re-deriving synthesized LogicalIds
  instead of reading them from the map.
- Domain reload clears static state; anything sync-critical held only in memory is a latent bug —
  persistence to `sbstate.json`/sidecar is mandatory, not an optimization.
- "Both changed since checkpoint" is a genuine conflict — surface it (§7), never auto-merge or
  last-write-wins.
