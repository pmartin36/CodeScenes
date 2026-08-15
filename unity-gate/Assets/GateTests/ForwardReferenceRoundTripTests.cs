using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;

// The full Unity confirmation checklist (specs/43-reference-forward-declaration.md) for the
// declare-before-use emit-order fix, against a real editor scene: 9 forward-reference cases
// (a reference to a later-declared local must compile, resolve, and converge to a fixed point)
// plus 6 regression cases (the emit-order change perturbs no pre-existing scene). Every case is
// driven through EmittedCodeCompiles.SyncAndAssertCompiles so the compile assertion (no CS0841 /
// CS0103) is inherited on every sync, never opted into per test.
public class ForwardReferenceRoundTripTests
{
    private const string ScenePath = "Assets/GateTests/__ForwardReferenceRoundTripTemp.unity";

    private string _dir;
    private string _builderPath;
    private string _sidecarPath;

    private static string Source(string body) => $@"
using UnityEngine;
using SceneBuilder.Authoring;
public class ForwardReferenceRoundTripScene : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
{body}
    }}
}}";

    private static GameObject Find(Scene scene, string path)
    {
        var parts = path.Split('/');
        var root = scene.GetRootGameObjects().FirstOrDefault(go => go.name == parts[0]);
        var t = root == null ? null : root.transform;
        for (var i = 1; t != null && i < parts.Length; i++)
        {
            t = t.Find(parts[i]);
        }
        return t == null ? null : t.gameObject;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb_frrt_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _builderPath = Path.Combine(_dir, "ForwardReferenceRoundTripScene.cs");
        _sidecarPath = Path.Combine(_dir, "ForwardReferenceRoundTripScene.sbmap.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private void SeedFromSource(string body)
    {
        File.WriteAllText(_builderPath, Source(body));
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();
        SceneBuilderBuild.Run(_builderPath, ScenePath, _sidecarPath, scene);
    }

    private void SeedFromEmpty() => SeedFromSource("");

    // Saves before every sync: cross-object reference fields resolve against the scene's
    // persisted identity, mirroring the added-child reference precedent
    // (AddedChildReferenceRoundTripTests) rather than the reference-free structural edits that
    // read straight off the live in-memory scene.
    private SceneBuilderSync.SyncResult Sync()
    {
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        return EmittedCodeCompiles.SyncAndAssertCompiles(_builderPath, _sidecarPath, EditorSceneManager.GetActiveScene());
    }

    // 1. A converged Opener carries a DoorOpener with the reference field unauthored. A new root
    // Door is added AFTER Opener, the reference is wired, and TWO syncs run: the first appends
    // Door, the second folds the reference and must hoist Door's declaration above the
    // referencing component.
    [Test]
    public void Introduce_NewRootTargetAfterReferrer_TwoSyncs_Compiles()
    {
        SeedFromSource(
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>();");

        new GameObject("Door");
        Sync();

        var opener = Find(EditorSceneManager.GetActiveScene(), "Opener");
        var door = Find(EditorSceneManager.GetActiveScene(), "Door");
        Assert.IsNotNull(opener, "Opener was not created by SceneBuilderBuild.Run");
        Assert.IsNotNull(door, "Door was not appended by the first sync");
        opener.GetComponent<DoorOpener>().target = door;
        Sync();

        var written = File.ReadAllText(_builderPath);
        var doorIndex = written.IndexOf("Add(\"Door\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(doorIndex >= 0, "Door's declaration is missing from emitted source:\n" + written);
        Assert.IsTrue(doorIndex < componentIndex,
            "Door's declaration must precede the referencing component:\n" + written);
        StringAssert.Contains("c.Set(\"target\", door)", written,
            "The reference did not fold onto the opener's component.\n" + written);

        var roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
        Assert.AreEqual("Opener", roots[0].name);
        Assert.AreEqual("Door", roots[1].name);

        var third = Sync();
        Assert.AreEqual(0, third.PatchEdits, "A third sync over the converged forward reference must be a fixed point.");
    }

    // 2. From scratch, a parent's own component references its own child, in ONE sync.
    [Test]
    public void FromScratch_ParentComponentRefsOwnChild_OneSync_Compiles()
    {
        SeedFromEmpty();

        var parent = new GameObject("Parent");
        var child = new GameObject("Child");
        child.transform.SetParent(parent.transform);
        var opener = parent.AddComponent<DoorOpener>();
        opener.target = child;

        Sync();

        var written = File.ReadAllText(_builderPath);
        var childIndex = written.IndexOf("Add(\"Child\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(childIndex >= 0, "Child's declaration is missing from emitted source:\n" + written);
        Assert.IsTrue(childIndex < componentIndex,
            "Child's declaration must precede the referencing component:\n" + written);
        StringAssert.DoesNotContain("parent2", written,
            "The child must head its own handle, not force a numeric-suffixed duplicate of the parent's receiver.\n" + written);
        StringAssert.Contains("c.Set(\"target\", child)", written,
            "The reference to the own child did not resolve to a real handle.\n" + written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The own-child forward reference must converge to a fixed point.");
    }

    // 3. Item 2 reached the other way: converge the parent, child, and an unauthored component
    // field first, then wire the reference on a second sync (the introduce path). Must agree
    // with the from-scratch shape in case 2.
    [Test]
    public void Introduce_ParentComponentRefsOwnChild_ReachedByWiringSecondSync_AgreesWithFromScratch()
    {
        SeedFromSource(
            "        var parent = scene.Add(\"Parent\");\n" +
            "        parent.Component<DoorOpener>();\n" +
            "        parent.Add(\"Child\");");

        var parent = Find(EditorSceneManager.GetActiveScene(), "Parent");
        var child = Find(EditorSceneManager.GetActiveScene(), "Parent/Child");
        Assert.IsNotNull(child, "Parent/Child was not created by SceneBuilderBuild.Run");
        parent.GetComponent<DoorOpener>().target = child;

        Sync();

        var written = File.ReadAllText(_builderPath);
        var childIndex = written.IndexOf("Add(\"Child\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(childIndex >= 0 && childIndex < componentIndex,
            "The child's declaration must precede the referencing component on the introduce path too:\n" + written);
        StringAssert.Contains("c.Set(\"target\", child)", written,
            "The introduced reference did not fold onto the parent's component.\n" + written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The introduce-path hoist must converge to a fixed point.");
    }

    // 4. An earlier root references a later, non-hierarchical sibling.
    [Test]
    public void FromScratch_EarlierRootRefsLaterSibling_Compiles()
    {
        SeedFromEmpty();

        var o1 = new GameObject("O1");
        new GameObject("O2");
        var opener = o1.AddComponent<DoorOpener>();
        opener.target = Find(EditorSceneManager.GetActiveScene(), "O2");

        Sync();

        var written = File.ReadAllText(_builderPath);
        var o2Index = written.IndexOf("Add(\"O2\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(o2Index >= 0 && o2Index < componentIndex,
            "The later sibling's declaration must precede the earlier root's referencing component:\n" + written);
        StringAssert.Contains("c.Set(\"target\", o2)", written);

        var roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
        Assert.AreEqual("O1", roots[0].name);
        Assert.AreEqual("O2", roots[1].name);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The cross-sibling forward reference must converge to a fixed point.");
    }

    // 5. A component references a descendant deeper than one level (a grandchild).
    [Test]
    public void FromScratch_ComponentRefsGrandchild_DeclOrderingHolds()
    {
        SeedFromEmpty();

        var root = new GameObject("Root");
        var opener = root.AddComponent<DoorOpener>();
        var child = new GameObject("Child");
        child.transform.SetParent(root.transform);
        var grandchild = new GameObject("Grandchild");
        grandchild.transform.SetParent(child.transform);
        opener.target = grandchild;

        Sync();

        var written = File.ReadAllText(_builderPath);
        var grandchildIndex = written.IndexOf("Add(\"Grandchild\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(grandchildIndex >= 0, "Grandchild's declaration is missing from emitted source:\n" + written);
        Assert.IsTrue(grandchildIndex < componentIndex,
            "The grandchild's declaration (and its parent chain) must precede the referencing component:\n" + written);
        StringAssert.Contains("c.Set(\"target\", grandchild)", written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The grandchild forward reference must converge to a fixed point.");
    }

    // 6. A node carries TWO DoorOpener components, the forward-referring one authored first.
    // Sync twice, then verify no component reorder churn.
    [Test]
    public void FromScratch_TwoDoorOpeners_ForwardReferrerFirst_NoComponentReorderChurn()
    {
        SeedFromEmpty();

        var go = new GameObject("Go");
        var opener1 = go.AddComponent<DoorOpener>();
        go.AddComponent<DoorOpener>();
        new GameObject("Later");
        opener1.target = Find(EditorSceneManager.GetActiveScene(), "Later");

        Sync();
        Sync();

        var written = File.ReadAllText(_builderPath);
        var firstComponentIndex = written.IndexOf(
            "Component<DoorOpener>(c => c.Set(\"target\", later))", System.StringComparison.Ordinal);
        var secondComponentIndex = written.IndexOf("Component<DoorOpener>();", System.StringComparison.Ordinal);
        Assert.IsTrue(firstComponentIndex >= 0, "The forward-referring component's fold is missing from emitted source:\n" + written);
        Assert.IsTrue(secondComponentIndex >= 0, "The bare second component is missing from emitted source:\n" + written);
        Assert.IsTrue(firstComponentIndex < secondComponentIndex,
            "The two DoorOpener components must keep their authored order:\n" + written);

        var third = Sync();
        Assert.AreEqual(0, third.PatchEdits, "The two-component forward reference must converge to a fixed point.");
    }

    // 7. One component with MIXED fields in a single closure: a forward reference, a backward
    // reference, and a plain value. All three must fold into ONE Component<MixedRefWiring> call.
    [Test]
    public void FromScratch_MixedFieldsOneClosure_ForwardBackwardPlain_Compiles()
    {
        SeedFromEmpty();

        new GameObject("Earlier");
        var owner = new GameObject("Owner");
        var mixed = owner.AddComponent<MixedRefWiring>();
        mixed.backward = Find(EditorSceneManager.GetActiveScene(), "Earlier");
        mixed.weight = 42;
        new GameObject("Later");
        mixed.forward = Find(EditorSceneManager.GetActiveScene(), "Later");

        Sync();

        var written = File.ReadAllText(_builderPath);
        var laterIndex = written.IndexOf("Add(\"Later\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<MixedRefWiring>", System.StringComparison.Ordinal);
        Assert.IsTrue(laterIndex >= 0 && laterIndex < componentIndex,
            "The forward target's declaration must precede the whole mixed-field component:\n" + written);
        StringAssert.Contains("c.Set(\"forward\", later)", written);
        StringAssert.Contains("c.Set(\"backward\", earlier)", written);
        StringAssert.Contains("c.Set(\"weight\", 42)", written);
        Assert.AreEqual(1, CountOccurrences(written, "Component<MixedRefWiring>"),
            "The forward, backward, and plain fields must fold into ONE component closure, not split across calls:\n" + written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The mixed-field forward reference must converge to a fixed point.");
    }

    // 8. Two same-batch roots cross-reference in ONE sync.
    [Test]
    public void FromScratch_TwoNodesCrossReference_ResolveInOneSync_Compiles()
    {
        SeedFromEmpty();

        var a = new GameObject("A");
        new GameObject("B");
        var openerA = a.AddComponent<DoorOpener>();
        openerA.target = Find(EditorSceneManager.GetActiveScene(), "B");

        Sync();

        var written = File.ReadAllText(_builderPath);
        var bIndex = written.IndexOf("Add(\"B\")", System.StringComparison.Ordinal);
        var componentIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        Assert.IsTrue(bIndex >= 0 && bIndex < componentIndex,
            "B's declaration must precede A's referencing component:\n" + written);
        StringAssert.Contains("c.Set(\"target\", b)", written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The two-root cross-reference must converge to a fixed point.");
    }

    // 9. A reference target that is a child of a parent declared after the referrer.
    [Test]
    public void FromScratch_TargetIsChildOfLaterDeclaredParent_ConvergesNoCS0841()
    {
        SeedFromEmpty();

        var referrer = new GameObject("Referrer");
        var opener = referrer.AddComponent<DoorOpener>();
        var laterParent = new GameObject("LaterParent");
        var laterChild = new GameObject("LaterChild");
        laterChild.transform.SetParent(laterParent.transform);
        opener.target = laterChild;

        Sync();
        Sync();

        var written = File.ReadAllText(_builderPath);
        StringAssert.Contains("c.Set(\"target\", laterChild)", written,
            "The reference to the later-declared parent's child never resolved to a real handle.\n" + written);

        var third = Sync();
        Assert.AreEqual(0, third.PatchEdits, "The deep forward reference must converge to a fixed point.");
    }

    // 10. A backward reference (a child's component references its parent) folds in place with
    // no hoist.
    [Test]
    public void Regression_BackwardReference_FoldsInPlace_NoHoist()
    {
        SeedFromEmpty();

        var parent = new GameObject("Parent");
        var child = new GameObject("Child");
        child.transform.SetParent(parent.transform);
        var opener = child.AddComponent<DoorOpener>();
        opener.target = parent;

        Sync();
        var first = File.ReadAllText(_builderPath);

        var second = Sync();
        var afterSecond = File.ReadAllText(_builderPath);

        Assert.AreEqual(0, second.PatchEdits, "A backward reference must fold in place with no further churn.");
        Assert.AreEqual(first, afterSecond,
            "The settling sync changed already-correct backward-reference source -- a hoist/reorder crept in where none was needed.");
    }

    // 11. A reference to an object already declared earlier: ordinary in-place fold, no churn.
    [Test]
    public void Regression_AlreadyDeclaredTarget_InPlaceFold_NoChurn()
    {
        SeedFromSource(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>();");

        var door = Find(EditorSceneManager.GetActiveScene(), "Door");
        var opener = Find(EditorSceneManager.GetActiveScene(), "Opener");
        opener.GetComponent<DoorOpener>().target = door;

        Sync();

        var written = File.ReadAllText(_builderPath);
        StringAssert.Contains("c.Set(\"target\", door)", written);
        Assert.AreEqual(1, CountOccurrences(written, "scene.Add(\"Door\")"),
            "An already-earlier-declared target must fold in place, not gain a duplicate/hoisted declaration:\n" + written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The in-place fold must converge to a fixed point.");
    }

    // 12. A pre-existing converged scene with references AND multiple components across several
    // nodes, re-synced with no scene change, must produce ZERO edits.
    [Test]
    public void Regression_PreexistingConvergedMultiNodeMultiComponentScene_ReSync_ZeroEdits()
    {
        SeedFromSource(
            "        var door = scene.Add(\"Door\");\n" +
            "        var opener = scene.Add(\"Opener\");\n" +
            "        opener.Component<DoorOpener>(c => c.Set(\"target\", door));\n" +
            "        opener.Component<Rigidbody>();\n" +
            "        var lamp = scene.Add(\"Lamp\");\n" +
            "        lamp.Component<Light>();");

        var result = Sync();

        Assert.AreEqual(0, result.PatchEdits,
            "Re-syncing an already-converged multi-node, multi-component, referencing scene with no scene "
            + "change must produce zero patch edits.");
        Assert.IsFalse(result.Changed, "An already-converged scene must not rewrite the builder source.");
    }

    // 13. A multi-component node with no forward reference keeps its components in scene order.
    [Test]
    public void Regression_MultiComponentNodeNoForwardRef_KeepsSceneOrder()
    {
        SeedFromEmpty();

        var go = new GameObject("Go");
        go.AddComponent<DoorOpener>();
        go.AddComponent<Rigidbody>();

        Sync();

        var written = File.ReadAllText(_builderPath);
        var doorOpenerIndex = written.IndexOf("Component<DoorOpener>", System.StringComparison.Ordinal);
        var rigidbodyIndex = written.IndexOf("Component<UnityEngine.Rigidbody>", System.StringComparison.Ordinal);
        Assert.IsTrue(doorOpenerIndex >= 0 && rigidbodyIndex >= 0,
            "Both components are missing from emitted source:\n" + written);
        Assert.IsTrue(doorOpenerIndex < rigidbodyIndex,
            "A multi-component node with no forward reference must keep the components in scene order:\n" + written);

        var second = Sync();
        Assert.AreEqual(0, second.PatchEdits, "The unrelated multi-component node must converge to a fixed point.");
    }

    // 14. Scene-side sibling reorders: drag the later target sibling above the referrer, sync;
    // then drag an unrelated sibling between referrer and target, sync. The reference must stay
    // valid and compiling throughout, and the scene must settle to a fixed point.
    [Test]
    public void Regression_SceneSideSiblingReorders_RoundTrip_OnlySiblingMoveEdits()
    {
        SeedFromEmpty();

        var referrer = new GameObject("Referrer");
        var target = new GameObject("Target");
        var unrelated = new GameObject("Unrelated");
        var opener = referrer.AddComponent<DoorOpener>();
        opener.target = target;

        Sync();

        target.transform.SetSiblingIndex(0);
        Sync();

        var afterFirstReorder = File.ReadAllText(_builderPath);
        StringAssert.Contains("c.Set(\"target\", target)", afterFirstReorder,
            "The reference must survive the sibling reorder and still compile.\n" + afterFirstReorder);

        unrelated.transform.SetSiblingIndex(1);
        Sync();

        var afterSecondReorder = File.ReadAllText(_builderPath);
        StringAssert.Contains("c.Set(\"target\", target)", afterSecondReorder,
            "The reference must survive an unrelated sibling reorder and still compile.\n" + afterSecondReorder);

        var settled = Sync();
        Assert.AreEqual(0, settled.PatchEdits, "After both sibling reorders, a further sync must be a fixed point.");
    }

    // 15. A scene with no references at all, re-synced with no scene change, must produce ZERO edits.
    [Test]
    public void Regression_NoReferencesScene_ReSync_ZeroEdits()
    {
        SeedFromSource(
            "        var parent = scene.Add(\"Parent\");\n" +
            "        parent.Add(\"Child\").Component<Rigidbody>();");

        var result = Sync();

        Assert.AreEqual(0, result.PatchEdits,
            "Re-syncing an already-converged reference-free scene with no scene change must produce zero patch edits.");
        Assert.IsFalse(result.Changed, "An already-converged reference-free scene must not rewrite the builder source.");
    }
}
