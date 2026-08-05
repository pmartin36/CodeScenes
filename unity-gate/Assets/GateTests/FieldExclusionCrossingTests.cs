using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneBuilder.Editor;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Materialize;
using SceneBuilder.Core.Model;
using Plan = SceneBuilder.Core.Plan;

// Spec 35 D2: the adapter populates the Core field-exclusion crossing (IFieldExclusionPolicy) onto
// every SceneSnapshot the real code->scene and scene->code producers build, keyed on the SAME
// serialized-field rule SerializedFieldExclusions.IsExcluded already applies on the live read, so
// the two directions can never diverge on what is "not author intent". Real GameObject/
// SerializedObject/scene state throughout - the Unity boundary the headless Core suite cannot see.
public class FieldExclusionCrossingTests
{
    private const string ScenePath = "Assets/GateTests/__FieldExclusionCrossingTemp.unity";

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(ScenePath))
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }
    }

    private static void SaveActiveScene() =>
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);

    private static string RootSegment(string propertyPath)
    {
        var dot = propertyPath.IndexOf('.');
        var bracket = propertyPath.IndexOf('[');
        var end = dot < 0 ? bracket : bracket < 0 ? dot : Math.Min(dot, bracket);
        return end < 0 ? propertyPath : propertyPath.Substring(0, end);
    }

    // ---- (1) base chain, not bare name -------------------------------------------------------

    [Test]
    public void Policy_SortingLayer_ExcludedThroughBaseChain_NotBareName()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var policy = SceneSnapshotReader.Read(scene, resolveSceneRef: null).FieldExclusions;

        Assert.IsTrue(policy.IsExcluded(new TypeRef("UnityEngine.SpriteRenderer"), "m_SortingLayer"),
            "SpriteRenderer inherits the Renderer-level m_SortingLayer exclusion.");
        Assert.IsTrue(policy.IsExcluded(new TypeRef("UnityEngine.MeshRenderer"), "m_SortingLayer"),
            "MeshRenderer inherits the Renderer-level m_SortingLayer exclusion.");
        Assert.IsTrue(policy.IsExcluded(new TypeRef("UnityEngine.Rendering.SortingGroup"), "m_SortingLayer"),
            "SortingGroup carries its own m_SortingLayer exclusion entry.");
        Assert.IsFalse(policy.IsExcluded(new TypeRef("UnityEngine.Canvas"), "m_SortingLayer"),
            "Canvas serializes only m_SortingLayerID - a bare-name rule would wrongly exclude it too.");
        Assert.IsFalse(policy.IsExcluded(new TypeRef("UnityEngine.Rigidbody"), "m_SortingLayer"),
            "Rigidbody is unrelated to sorting - a bare-name rule would wrongly exclude it too.");
    }

    // ---- (2) type-qualified m_Size survives the crossing --------------------------------------

    [Test]
    public void Policy_Size_TypeQualified_NotBareName()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var policy = SceneSnapshotReader.Read(scene, resolveSceneRef: null).FieldExclusions;

        Assert.IsTrue(policy.IsExcluded(new TypeRef("UnityEngine.SpriteRenderer"), "m_Size"),
            "SpriteRenderer.m_Size is recomputed from the sprite bounds and must stay excluded.");
        Assert.IsFalse(policy.IsExcluded(new TypeRef("UnityEngine.BoxCollider2D"), "m_Size"),
            "BoxCollider2D genuinely authors its own m_Size - a bare-name rule would wrongly exclude it.");
        Assert.IsFalse(policy.IsExcluded(new TypeRef("UnityEngine.UI.Scrollbar"), "m_Size"),
            "Scrollbar genuinely authors its own m_Size - a bare-name rule would wrongly exclude it.");
    }

    // ---- (3) a type absent from the live scene still gets an answer ---------------------------

    [Test]
    public void Policy_ComponentTypeAbsentFromLiveScene_StillAnswers()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Empty");
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var snapshot = SceneSnapshotReader.Read(scene, resolveSceneRef: null);

        var hasSpriteRenderer = snapshot.Roots
            .SelectMany(FlattenComponentTypeNames)
            .Any(t => t == "UnityEngine.SpriteRenderer");
        Assert.IsFalse(hasSpriteRenderer,
            "Test premise invalid: the live snapshot must not already carry a SpriteRenderer for " +
            "this case to prove anything.");

        Assert.IsTrue(snapshot.FieldExclusions.IsExcluded(new TypeRef("UnityEngine.SpriteRenderer"), "m_Size"),
            "The policy must answer for a component type the live scene never had - the first build " +
            "in a session creates it, and the create path needs a decision with no snapshot presence " +
            "to key off.");
    }

    private static IEnumerable<string> FlattenComponentTypeNames(SnapshotNode node)
    {
        foreach (var c in node.Components)
        {
            yield return c.Type.FullName;
        }

        foreach (var child in node.Children)
        {
            foreach (var name in FlattenComponentTypeNames(child))
            {
                yield return name;
            }
        }
    }

    // ---- (3b) create path, end to end through the real seam -----------------------------------

    [Test]
    public void CreatePath_ExcludedFieldOnNewComponent_EmitsNoOp_SiblingFieldStillEmits_AuditIsClean()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var scene = EditorSceneManager.GetActiveScene();

        const string source = @"
using SceneBuilder.Authoring;
public class FieldExclusionCrossingCreateScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""Sprite"").Component<UnityEngine.SpriteRenderer>(c =>
        {
            c.Set(""m_SortingLayer"", 1);
            c.Set(""m_SortingLayerID"", 0);
        });
    }
}";

        var loaded = DesiredModelLoader.Load(source, null);
        var remapped = IdentityRemapper.Remap(loaded.Parse.Model, new IdentityMap());
        var sceneRef = ObjectReferenceResolver.BuildSceneRefResolver(remapped);
        var snapshot = SceneSnapshotReader.Read(scene, sceneRef);
        var plan = Materializer.Materialize(loaded.Desired, snapshot, remapped);

        var excludedOps = plan.Ops
            .OfType<Plan.SetField>()
            .Where(op => op.Path == "m_SortingLayer" ||
                         op.Path.StartsWith("m_SortingLayer.", StringComparison.Ordinal) ||
                         op.Path.StartsWith("m_SortingLayer[", StringComparison.Ordinal))
            .ToArray();
        Assert.IsEmpty(excludedOps,
            "The create path (component not present in the live snapshot at all) must not emit a " +
            "plan op carrying an excluded field.");

        Assert.IsTrue(plan.Ops.OfType<Plan.SetField>().Any(op => op.Path == "m_SortingLayerID"),
            "A sibling field on the same authored component must still emit normally.");

        var offenders = ExcludedFieldAudit.EmittedExclusions(plan, loaded.Desired, snapshot.FieldExclusions);
        Assert.IsEmpty(offenders,
            "The bypass audit must find no op carrying an excluded field anywhere in the final " +
            "plan. Offenders: " + string.Join(", ", offenders));
    }

    // ---- (4) incremental == cold ---------------------------------------------------------------

    [Test]
    public void Policy_ColdAndIncrementalAssemble_ShareInstance_AndAnswerIdentically()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("Sprite");
        go.AddComponent<SpriteRenderer>();
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var css = new ChangeScopedSnapshot();
        var cold = css.AssembleCold(scene, SceneRefResolver.None);

        go.tag = "Player";
        SaveActiveScene();
        var incremental = css.AssembleIncremental(scene, new[] { go.GetEntityId() }, SceneRefResolver.None);

        var readerPolicy = SceneSnapshotReader.Read(scene, resolveSceneRef: null).FieldExclusions;

        Assert.AreSame(cold.FieldExclusions, incremental.FieldExclusions,
            "Cold and incremental assemble must attach the SAME policy instance so they cannot diverge.");
        Assert.AreSame(cold.FieldExclusions, readerPolicy,
            "The cold read path and ChangeScopedSnapshot must attach the same policy instance.");

        foreach (var (typeName, root, expected) in new[]
        {
            ("UnityEngine.SpriteRenderer", "m_SortingLayer", true),
            ("UnityEngine.SpriteRenderer", "m_Size", true),
            ("UnityEngine.BoxCollider2D", "m_Size", false),
            ("UnityEngine.Canvas", "m_SortingLayer", false),
        })
        {
            Assert.AreEqual(expected, cold.FieldExclusions.IsExcluded(new TypeRef(typeName), root),
                $"cold: {typeName}.{root}");
            Assert.AreEqual(expected, incremental.FieldExclusions.IsExcluded(new TypeRef(typeName), root),
                $"incremental: {typeName}.{root}");
        }
    }

    // ---- (5a-live) every producer returns the adapter's policy, never the Core default --------

    [Test]
    public void Policy_EveryProducer_ReturnsAdapterPolicy_NotNoFieldExclusions()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SaveActiveScene();
        var scene = EditorSceneManager.GetActiveScene();

        var coldRead = SceneSnapshotReader.Read(scene, resolveSceneRef: null);
        var css = new ChangeScopedSnapshot();
        var assembleCold = css.AssembleCold(scene, SceneRefResolver.None);
        var assembleIncremental = css.AssembleIncremental(scene, Array.Empty<EntityId>(), SceneRefResolver.None);

        foreach (var (label, snapshot) in new (string, SceneSnapshot)[]
        {
            ("SceneSnapshotReader.Read", coldRead),
            ("ChangeScopedSnapshot.AssembleCold", assembleCold),
            ("ChangeScopedSnapshot.AssembleIncremental", assembleIncremental),
        })
        {
            Assert.AreSame(SerializedFieldExclusions.Policy.Instance, snapshot.FieldExclusions,
                $"{label} must attach the adapter's SerializedFieldExclusions.Policy singleton.");
            Assert.AreNotSame(NoFieldExclusions.Instance, snapshot.FieldExclusions,
                $"{label} must not leave the Core default (nothing excluded) live on a real snapshot.");
        }
    }

    // ---- (5a-static) SceneSnapshot is constructed, and FieldExclusions is set, in one place ---

    private static readonly Regex StringLiteral = new Regex("\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Compiled);
    private static readonly Regex FieldExclusionsBoundary =
        new Regex(@"(?<![A-Za-z0-9_])FieldExclusions(?![A-Za-z0-9_])", RegexOptions.Compiled);

    [Test]
    public void Package_SceneSnapshotConstructedOnlyInReader_AndFieldExclusionsSetOnlyThere()
    {
        var root = SceneBuilderPaths.PackageRootPath;
        Assert.IsNotNull(root, "SceneBuilderPaths.PackageRootPath resolved to null - cannot scan the " +
            "package (this must FAIL, never vacuously pass).");

        var newSnapshotHits = new HashSet<string>();
        var fieldExclusionsHits = new HashSet<string>();

        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = StringLiteral.Replace(lines[i], "\"\"");
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                if (line.Contains("new SceneSnapshot"))
                {
                    newSnapshotHits.Add(Path.GetFileName(file));
                }

                if (FieldExclusionsBoundary.IsMatch(line))
                {
                    fieldExclusionsHits.Add(Path.GetFileName(file));
                }
            }
        }

        CollectionAssert.AreEquivalent(new[] { "SceneSnapshotReader.cs" }, newSnapshotHits,
            "SceneSnapshot must be constructed in exactly one place (SceneSnapshotReader.FromRoots) " +
            "so the adapter's field-exclusion decision is attached by construction for every " +
            "producer, not opted into per site. Found in: " + string.Join(", ", newSnapshotHits));

        CollectionAssert.AreEquivalent(new[] { "SceneSnapshotReader.cs" }, fieldExclusionsHits,
            "SceneSnapshot.FieldExclusions must be set in exactly one place, so no second producer " +
            "can diverge on what is excluded. Found in: " + string.Join(", ", fieldExclusionsHits));
    }

    // ---- (5b-static) the crossing has exactly one implementation, in its declared owner -------

    [Test]
    public void Package_IFieldExclusionPolicyImplementedOnlyInDeclaredOwner()
    {
        var root = SceneBuilderPaths.PackageRootPath;
        Assert.IsNotNull(root, "SceneBuilderPaths.PackageRootPath resolved to null - cannot scan the " +
            "package (this must FAIL, never vacuously pass).");

        var hits = new HashSet<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("IFieldExclusionPolicy"))
            {
                hits.Add(Path.GetFileName(file));
            }
        }

        CollectionAssert.AreEquivalent(new[] { "SerializedFieldExclusions.cs" }, hits,
            "IFieldExclusionPolicy must be implemented in exactly one adapter file - the declared " +
            "owner of what serialized state is not author intent - so the exclusion set cannot be " +
            "independently re-derived elsewhere. Found in: " + string.Join(", ", hits));
    }

    // ---- (5b-behavioral) the policy IS the owner's predicate, swept over real serialized state -

    [Test]
    public void Policy_MatchesOwnerPredicate_AcrossRealSerializedState()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var probes = new Component[]
        {
            new GameObject("Sprite").AddComponent<SpriteRenderer>(),
            new GameObject("Mesh").AddComponent<MeshRenderer>(),
            new GameObject("Group").AddComponent<UnityEngine.Rendering.SortingGroup>(),
            new GameObject("Canvas").AddComponent<Canvas>(),
            new GameObject("Box").AddComponent<BoxCollider2D>(),
            new GameObject("Body").AddComponent<Rigidbody>(),
            new GameObject("FitSize").AddComponent<SceneBuilder.Authoring.FitSize>(),
        };
        SaveActiveScene();

        var scene = EditorSceneManager.GetActiveScene();
        var policy = SceneSnapshotReader.Read(scene, resolveSceneRef: null).FieldExclusions;

        var sawTrue = false;
        var sawFalse = false;
        var mismatches = new List<string>();

        foreach (var component in probes)
        {
            var type = component.GetType();
            var so = new SerializedObject(component);
            var it = so.GetIterator();
            var enterChildren = true;
            while (it.Next(enterChildren))
            {
                enterChildren = false;
                var root = RootSegment(it.propertyPath);
                var expected = SerializedFieldExclusions.IsExcluded(type, root);
                var actual = policy.IsExcluded(new TypeRef(type.FullName), root);

                if (expected)
                {
                    sawTrue = true;
                }
                else
                {
                    sawFalse = true;
                }

                if (expected != actual)
                {
                    mismatches.Add($"{type.FullName}.{root}: owner={expected} policy={actual}");
                }
            }
        }

        Assert.IsTrue(sawTrue, "Test premise invalid: the sweep never hit a root the owner excludes.");
        Assert.IsTrue(sawFalse, "Test premise invalid: the sweep never hit a root the owner allows.");
        Assert.IsEmpty(mismatches,
            "The adapter policy must answer identically to SerializedFieldExclusions.IsExcluded for " +
            "every root the live read prunes by. Mismatches: " + string.Join("; ", mismatches));
    }
}
