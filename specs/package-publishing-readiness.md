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

4. **Distribution format.** Licensing (`specs/34`) says Gumroad only. Decide the delivery artifact: a
   UPM tarball the buyer adds by path/URL, or a `.unitypackage` they import. (An Asset Store listing
   later would add its own submission requirements beyond this list.)

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

## Verified facts behind the above

- Manifest + asmdefs + folder metas present and correct (loads today).
- `Plugins/SceneBuilder.Core.dll.meta` is a bare stub with no `PluginImporter` block (checked).
- No `RoslynAnalyzer` label anywhere in `com.codescenes/`; analyzers are in the hidden `Analyzers~/`.
- No `LICENSE`, `README`, `CHANGELOG`, `Third Party Notices`, `Documentation~`, or `Samples~` at the
  package root (checked).
