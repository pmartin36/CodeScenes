using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // The emit-side decision of what to omit from a newly-authored field set because it
    // equals the type's constructed default (spec C2/C3's mechanism).
    public class ComponentDefaultOmissionTests
    {
        private static ComponentData[] RigidbodyTemplate(ValueNode mass) => new[]
        {
            new ComponentData
            {
                LogicalId = "template",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", mass) }),
            },
        };

        private static ComponentData[] RigidbodyTemplate(ValueNode mass, ValueNode useGravity) => new[]
        {
            new ComponentData
            {
                LogicalId = "template",
                Type = new TypeRef("UnityEngine.Rigidbody"),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>("m_Mass", mass),
                    new KeyValuePair<string, ValueNode>("m_UseGravity", useGravity),
                }),
            },
        };

        [Fact]
        public void IsDefault_ValueEqualsTemplate_ReturnsTrue()
        {
            var index = ComponentDefaultOmission.Index.Build(RigidbodyTemplate(ValueNode.Primitive.Float(1f)));

            Assert.True(index.IsDefault("UnityEngine.Rigidbody", "m_Mass", ValueNode.Primitive.Float(1f)));
        }

        [Fact]
        public void IsDefault_ValueDiffersFromTemplate_ReturnsFalse()
        {
            var index = ComponentDefaultOmission.Index.Build(RigidbodyTemplate(ValueNode.Primitive.Float(1f)));

            Assert.False(index.IsDefault("UnityEngine.Rigidbody", "m_Mass", ValueNode.Primitive.Float(5f)));
        }

        [Fact]
        public void IsDefault_TypeHasNoTemplate_ReturnsFalse()
        {
            // Empty.IsDefault must never claim "default" for a type it has no template for —
            // that would drop user-authored data.
            Assert.False(ComponentDefaultOmission.Index.Empty.IsDefault("UnityEngine.Rigidbody", "m_Mass", ValueNode.Primitive.Float(1f)));
        }

        [Fact]
        public void OmitDefaults_AllFieldsAtTemplate_ReturnsEmptyFieldMap()
        {
            var index = ComponentDefaultOmission.Index.Build(RigidbodyTemplate(ValueNode.Primitive.Float(1f)));
            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(1f)) });

            var result = ComponentDefaultOmission.OmitDefaults("UnityEngine.Rigidbody", fields, index);

            Assert.Empty(result);
        }

        [Fact]
        public void OmitDefaults_OneFieldOffTemplate_KeepsOnlyThatField()
        {
            var index = ComponentDefaultOmission.Index.Build(
                RigidbodyTemplate(ValueNode.Primitive.Float(1f), ValueNode.Primitive.Bool(true)));
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                new KeyValuePair<string, ValueNode>("m_UseGravity", ValueNode.Primitive.Bool(true)),
            });

            var result = ComponentDefaultOmission.OmitDefaults("UnityEngine.Rigidbody", fields, index);

            var kept = Assert.Single(result);
            Assert.Equal("m_Mass", kept.Key);
            Assert.Equal(ValueNode.Primitive.Float(5f), kept.Value);
        }

        [Fact]
        public void IsDefault_TypeHasTemplateButFieldKeyAbsent_ReturnsFalse()
        {
            // The type IS registered (m_Mass has a template value), but the queried key
            // (m_UseGravity) is absent from that template. Per the safety contract ("never claim
            // default for what you don't know"), an absent key must never be treated as default —
            // that would silently drop a field the emit side has no basis to omit.
            var index = ComponentDefaultOmission.Index.Build(RigidbodyTemplate(ValueNode.Primitive.Float(1f)));

            Assert.False(index.IsDefault("UnityEngine.Rigidbody", "m_UseGravity", ValueNode.Primitive.Bool(true)));
        }

        [Fact]
        public void OmitDefaults_NullIndex_KeepsEveryField()
        {
            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(1f)) });

            var result = ComponentDefaultOmission.OmitDefaults("UnityEngine.Rigidbody", fields, null);

            Assert.Equal(fields, result);
        }

        // ---- Integration: the real seam, Reconciler.Reconcile's append + introduce emission -----

        private static IdentityMap MapWithRoot() => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
            },
        };

        [Fact]
        public void Reconcile_AppendedComponentAllFieldsAtTypeDefault_AppendsBareComponentWithNoFields()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(1f)) });
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = fields },
                        },
                    },
                },
                ComponentDefaults = RigidbodyTemplate(ValueNode.Primitive.Float(1f)),
            };

            var result = Reconciler.Reconcile(model, snapshot, MapWithRoot());

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Empty(append.Fields);
        }

        [Fact]
        public void Reconcile_AppendedComponentOneFieldOffTypeDefault_AppendsOnlyThatField()
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
            };

            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                new KeyValuePair<string, ValueNode>("m_UseGravity", ValueNode.Primitive.Bool(true)),
            });
            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef("UnityEngine.Rigidbody"), Fields = fields },
                        },
                    },
                },
                // m_UseGravity matches the template default; m_Mass does not.
                ComponentDefaults = RigidbodyTemplate(ValueNode.Primitive.Float(1f), ValueNode.Primitive.Bool(true)),
            };

            var result = Reconciler.Reconcile(model, snapshot, MapWithRoot());

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            var kept = Assert.Single(append.Fields);
            Assert.Equal("m_Mass", kept.Key);
        }
    }
}
