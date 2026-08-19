# Package publishing readiness

**NOT a tdd-pipeline spec.** Do not run this through `task-deconstruct` or the tdd-pipeline. There is
nothing to build with RED tests. Every item below is a one-off file add, a manifest edit, or a
decision. Treat it as a checklist plus open questions for getting `com.codescenes/` from "loads in
Unity" to "shippable paid package."

Requirement tiers are Unity's own: [package layout](https://docs.unity3d.com/6000.0/Documentation/Manual/cus-layout.html),
[package manifest](https://docs.unity3d.com/6000.0/Documentation/Manual/upm-manifestPkg.html).

## Current state (done, do not redo)

Everything Unity strictly requires is in place, and the package loads (the EditMode gate and
live-verify prove it):
- `package.json` with the required `name` (`com.codescenes`) + `version` (`0.1.0`), plus
  `displayName`, `description`, `unity` (`6000.0`), `author`, `hideInEditor`.
- `.meta` files on `Editor/`, `Runtime/`, `Plugins/`.
- Two assembly definitions, correctly scoped: `Runtime/SceneBuilder.Authoring.asmdef` (auto-referenced)
  and `Editor/SceneBuilder.Editor.asmdef` (editor-only via `includePlatforms: ["Editor"]`, references
  Authoring).
- `Plugins/` carries `SceneBuilder.Core.dll` + `SceneBuilder.Grammar.dll` and the bundled third-party
  DLLs, each with a `.meta`.

No `Tests/` folder in the package is correct: tests live in `unity-gate/`, so the package ships none.

## Decisions needed (the discussion part)

These need a human call before the mechanical work is meaningful.

1. **License.** CodeScenes is paid, so this is a proprietary/commercial EULA, not an OSS SPDX id. Two
   parts: the actual license text (drafted here, or a reference to the codescenes.dev terms), and the
   manifest `license` field (proprietary packages conventionally use `"SEE LICENSE IN LICENSE.md"`).
   Note bundling MIT third-party DLLs inside a proprietary product is fine, but their attribution must
   ship (see Third Party Notices below).

2. **Analyzer delivery.** The analyzers sit in `Analyzers~/`; the `~` makes Unity ignore the folder,
   and no `.meta` carries a `RoslynAnalyzer` label, so CodeScenes diagnostics do **not** run inside a
   buyer's Unity editor today, only in the external builder `.csproj`. Given the builder lives outside
   `Assets/`, that may be intended. Decide: should diagnostics light up in the user's Unity (move them
   to a normal folder, add the `RoslynAnalyzer` label, set the `.meta` to no platforms), or is
   builder-`.csproj`-only the right delivery?

3. **Plugin platform settings.** The bundled DLL `.meta` files are stubs (`guid` only, no
   `PluginImporter` block), so a buyer's Unity regenerates platform settings on import. Decide whether
   to pin explicit `PluginImporter` platforms before shipping, chiefly to keep the editor-only Roslyn
   DLLs out of player builds, or to accept Unity's import-time defaults.

4. **Distribution format — DECIDED.** The off-store buyer installs the package via Unity Package
   Manager "Add package from git URL" pointing at the public monorepo subfolder:
   `https://github.com/pmartin36/CodeScenes.git?path=com.codescenes`. That install IS the free trial;
   Gumroad sells only the activation key, not a download. No `.unitypackage`, no tarball. The repo is
   public (confirmed by anonymous `ls-remote`), so the whole monorepo — Core, specs, docs — is public;
   that is accepted. The Asset Store copy is a separate keyless artifact (licensing stripped, see the
   two-build section) uploaded through Unity's own system. NOTE: git-URL install requires the package
   state to actually be pushed; `main` has run far ahead of `origin` in practice.

## Files to add (mechanical, once the decisions above are made)

Unity lists all of these as recommended, none block loading, but they are the "publishable package"
gap for a paid product:

- **`LICENSE.md`** + set the manifest `license` field. Highest priority: license is currently nowhere.
- **`Third Party Notices.md`** covering the 8 bundled third-party DLLs (all MIT-licensed Microsoft/.NET
  libraries): `Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`,
  `Microsoft.Bcl.AsyncInterfaces.dll`, `System.Collections.Immutable.dll`,
  `System.Reflection.Metadata.dll`, `System.Text.Json.dll`, `System.Text.Encodings.Web.dll`,
  `System.Text.Encoding.CodePages.dll`. Attribution is required even in a proprietary product.
- **`CHANGELOG.md`** in reverse-chronological order.
- **`README.md`**. For a paid tool this can be user-facing and point at the codescenes.dev docs.
- **Version bump** at publish time (`0.1.0` is a dev placeholder).
- Optional manifest polish: `documentationUrl` / `changelogUrl` / `licensesUrl` pointing at
  codescenes.dev; `keywords` (only useful on a registry/Asset Store).
- Optional `Documentation~/` and `Samples~/`. A `Samples~/` demo scene is worth having for a paid tool.

## Two-build production (compliance-required, NOT yet done)

**Why this is mandatory, not optional.** Unity Provider Agreement §4.9.1.2 (see
`CodeScenesSite/research/04-asset-store-distribution.md`) requires the Asset Store copy and the
off-store copy to be the same complete product, nothing enhanced sold off-store. So CodeScenes must
ship on BOTH channels, and the Asset Store copy must be **fully featured with no key, no activation,
no trial, and no backend contact** (Unity's own per-seat EULA covers it); the Gumroad copy is the same
product plus the activation layer. `specs/completed/34` "Two distribution channels, two builds" owns
the design.

**What is already built** (spec 34, bucket b4, commit `4756951`) — the channel MECHANISM, under test:
- `LicenseChannel.AssetStoreDefine` = `CODESCENES_ASSET_STORE`, in the MAIN assembly (observable in
  both builds).
- `SceneBuilder.Licensing.Editor.asmdef` carries `"defineConstraints": ["!CODESCENES_ASSET_STORE"]`,
  so with the define set the whole licensing assembly (window, seats, trial, enforcement provider,
  transport) is excluded from compilation.
- Fail-closed: no define -> Gumroad build, activation required. `LicenseGate` (main assembly) defaults
  allowed, and only the licensing assembly registers a restrictive verdict, so an excluded-licensing
  build is fully functional.

**The gap** — no tooling produces the two artifacts, and the `defineConstraint` alone does NOT protect
a buyer: it only excludes licensing when the project *doing the compile* defines
`CODESCENES_ASSET_STORE`, and an Asset Store buyer's project will not. Shipping the same files to the
Asset Store would compile the licensing assembly on their machine and demand a key they were never
issued, exactly the failure spec 34 warns about.

**Recommended method.** Produce the Asset Store variant by **physically removing
`com.codescenes/Editor/Licensing/`** (the folder and its `.meta`) from the packed copy, so licensing
cannot compile regardless of the buyer's defines. The define + `defineConstraint` remain the dev-time
way to compile and test the Asset-Store configuration locally (set `CODESCENES_ASSET_STORE` in a test
project, confirm licensing drops out). This wants a small repeatable pack step/script (two outputs:
Gumroad = licensing included, Asset Store = licensing stripped), tied to the distribution-format
decision (#4 above).

**Accept when:** the Asset Store artifact exposes no `CodeScenes/License` menu item, makes no request
to the backend, shows no activation/trial UI, and every feature works with `LicenseGate.Allowed`
true; the Gumroad artifact still requires activation; and the two are otherwise feature-identical.

## Verified facts behind the above

- Manifest + asmdefs + folder metas present and correct (loads today).
- `Plugins/SceneBuilder.Core.dll.meta` is a bare stub with no `PluginImporter` block (checked).
- No `RoslynAnalyzer` label anywhere in `com.codescenes/`; analyzers are in the hidden `Analyzers~/`.
- No `LICENSE`, `README`, `CHANGELOG`, `Third Party Notices`, `Documentation~`, or `Samples~` at the
  package root (checked).
