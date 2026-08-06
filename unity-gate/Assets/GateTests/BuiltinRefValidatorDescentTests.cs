using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SceneBuilder.Editor;
using SceneBuilder.Core.Model;

// BuiltinRefValidator.Validate's located pre-pass composes the reported object/component/field path
// by hand as it descends List and Nested containers. These tests drive Validate directly with values
// reachable ONLY through such a descent, pinning both the composed path string and which unresolvable
// reference is reported first.
public class BuiltinRefValidatorDescentTests
{
    private static FieldMap FM(params (string Key, ValueNode Value)[] pairs) =>
        new FieldMap(pairs.Select(p => new KeyValuePair<string, ValueNode>(p.Key, p.Value)));

    private static ValueNode Builtin(string name, string typeHint = "") =>
        new ValueNode.AssetRef(new AssetRef { DisplayPath = name, IsBuiltin = true, TypeHint = typeHint });

    private static SceneModel Model(string objectName, string componentTypeFullName, params (string Key, ValueNode Value)[] fields) =>
        new SceneModel
        {
            Roots = new[]
            {
                new GameObjectNode
                {
                    Name = objectName,
                    Components = new[]
                    {
                        new ComponentData
                        {
                            Type = new TypeRef(componentTypeFullName),
                            Fields = FM(fields),
                        },
                    },
                },
            },
        };

    [Test]
    public void Validate_UnresolvableBuiltinReachableOnlyThroughContainers_ThrowsNamingTheDescendedPath()
    {
        var model = Model("Widget", "UnityEngine.MeshFilter",
            ("payload", new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[] { Builtin("NoSuchBuiltinXyz") }))))));

        var ex = Assert.Throws<InvalidOperationException>(() => BuiltinRefValidator.Validate(model));
        StringAssert.Contains("Widget > MeshFilter.payload.mats[0]", ex.Message,
            "the descended path lost its container spellings");
        StringAssert.Contains("NoSuchBuiltinXyz", ex.Message);
    }

    [Test]
    public void Validate_ResolvableBuiltinInsideContainers_DoesNotThrow()
    {
        var model = Model("Widget", "UnityEngine.MeshFilter",
            ("payload", new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[] { Builtin("Cube") }))))));

        Assert.DoesNotThrow(() => BuiltinRefValidator.Validate(model),
            "a resolvable builtin reached through containers should not throw");
    }

    [Test]
    public void Validate_MultipleUnresolvableRefsUnderOneField_ReportsThePreOrderFirstOne()
    {
        var model = Model("Widget", "UnityEngine.MeshFilter",
            ("payload", new ValueNode.Nested("Game.Payload", FM(
                ("mats", new ValueNode.List(new ValueNode[] { Builtin("FirstBadName"), Builtin("SecondBadName") })),
                ("one", Builtin("ThirdBadName"))))));

        var ex = Assert.Throws<InvalidOperationException>(() => BuiltinRefValidator.Validate(model));
        StringAssert.Contains("payload.mats[0]", ex.Message, "the first-encountered bad ref was not the one reported");
        StringAssert.Contains("FirstBadName", ex.Message);
        StringAssert.DoesNotContain("SecondBadName", ex.Message);
        StringAssert.DoesNotContain("ThirdBadName", ex.Message);
    }

    [Test]
    public void Validate_BuiltinContainerPathAtASpecificListIndex_ThrowsNamingThatIndex()
    {
        var model = Model("Widget", "UnityEngine.MeshFilter",
            ("mats", new ValueNode.List(new ValueNode[]
            {
                ValueNode.Primitive.Int(0),
                new ValueNode.AssetRef(new AssetRef { DisplayPath = BuiltinCatalog.BuiltinResourcesPath, IsBuiltin = false }),
            })));

        var ex = Assert.Throws<InvalidOperationException>(() => BuiltinRefValidator.Validate(model));
        StringAssert.Contains("Widget > MeshFilter.mats[1]", ex.Message,
            "the re-derived index counter did not match the container position");
        StringAssert.Contains(BuiltinCatalog.BuiltinResourcesPath, ex.Message);
    }

    [Test]
    public void Validate_AlreadyResolvedRefInsideAContainer_IsSkipped()
    {
        var model = Model("Widget", "UnityEngine.MeshFilter",
            ("mats", new ValueNode.List(new ValueNode[]
            {
                new ValueNode.AssetRef(new AssetRef { Guid = "deadbeef", DisplayPath = "NoSuchBuiltinXyz", IsBuiltin = true }),
            })));

        Assert.DoesNotThrow(() => BuiltinRefValidator.Validate(model),
            "an already-resolved ref (non-empty Guid) inside a container must be skipped");
    }

    [Test]
    public void Validate_BadRefOnAChildObjectThreeLevelsDeep_ThrowsNamingObjectComponentAndFullPath()
    {
        var model = new SceneModel
        {
            Roots = new[]
            {
                new GameObjectNode
                {
                    Name = "Parent",
                    Children = new[]
                    {
                        new GameObjectNode
                        {
                            Name = "Kid",
                            Components = new[]
                            {
                                new ComponentData
                                {
                                    Type = new TypeRef("UnityEngine.MeshRenderer"),
                                    Fields = FM(("payload", new ValueNode.Nested("Game.Payload", FM(
                                        ("mats", new ValueNode.List(new ValueNode[]
                                        {
                                            new ValueNode.Nested("Game.Inner", FM(("deep", Builtin("DeepBadName")))),
                                        })))))),
                                },
                            },
                        },
                    },
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BuiltinRefValidator.Validate(model));
        StringAssert.Contains("Kid > MeshRenderer.payload.mats[0].deep", ex.Message,
            "descending into a child object's containers lost the object name, component, or full path");
        StringAssert.Contains("DeepBadName", ex.Message);
    }
}
