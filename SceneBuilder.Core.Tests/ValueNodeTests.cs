using System.Collections.Generic;
using SceneBuilder.Core.Model;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class ValueNodeTests
    {
        [Fact]
        public void Primitive_TagDistinguishesIntLongFloatDouble_Unequal()
        {
            var i = new ValueNode.Primitive(PrimitiveKind.Int, 12);
            var l = new ValueNode.Primitive(PrimitiveKind.Long, 12L);
            var f = new ValueNode.Primitive(PrimitiveKind.Float, 12f);
            var d = new ValueNode.Primitive(PrimitiveKind.Double, 12d);

            Assert.NotEqual(i, l);
            Assert.NotEqual(i, f);
            Assert.NotEqual(i, d);
            Assert.NotEqual(f, d);

            Assert.Equal(new ValueNode.Primitive(PrimitiveKind.Int, 12), i);
        }

        [Fact]
        public void Vec3_SameValue_Equal()
        {
            var a = new ValueNode.Vec3(new Vec3(1, 2, 3));
            var b = new ValueNode.Vec3(new Vec3(1, 2, 3));

            Assert.Equal(a, b);
        }

        [Fact]
        public void Vec3_OneUlpDifference_NotEqual()
        {
            var z = 3f;
            var zNextUlp = System.MathF.BitIncrement(z);
            Assert.NotEqual(z, zNextUlp); // sanity: literal really is a distinct float

            var a = new ValueNode.Vec3(new Vec3(1, 2, z));
            var b = new ValueNode.Vec3(new Vec3(1, 2, zNextUlp));

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void List_DeepOrderSignificantEquality()
        {
            var a = new ValueNode.Primitive(PrimitiveKind.Int, 1);
            var b = new ValueNode.Primitive(PrimitiveKind.Int, 2);

            var ab = new ValueNode.List(new ValueNode[] { a, b });
            var abAgain = new ValueNode.List(new ValueNode[] { a, b });
            var ba = new ValueNode.List(new ValueNode[] { b, a });
            var aOnly = new ValueNode.List(new ValueNode[] { a });

            Assert.Equal(ab, abAgain);
            Assert.NotEqual(ab, ba);
            Assert.NotEqual(ab, aOnly);
        }

        [Fact]
        public void Nested_FieldMap_DeepKeyOrderSignificantEquality()
        {
            var x = new ValueNode.Primitive(PrimitiveKind.Int, 1);
            var y = new ValueNode.Primitive(PrimitiveKind.Int, 2);

            var mapXY = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("x", x),
                new KeyValuePair<string, ValueNode>("y", y),
            });
            var mapXYAgain = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("x", x),
                new KeyValuePair<string, ValueNode>("y", y),
            });
            var mapYX = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("y", y),
                new KeyValuePair<string, ValueNode>("x", x),
            });
            var mapXOnly = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("x", x),
            });

            var nested = new ValueNode.Nested(mapXY);
            var nestedAgain = new ValueNode.Nested(mapXYAgain);
            var nestedReordered = new ValueNode.Nested(mapYX);
            var nestedFewer = new ValueNode.Nested(mapXOnly);

            Assert.Equal(nested, nestedAgain);
            Assert.NotEqual(nested, nestedReordered);
            Assert.NotEqual(nested, nestedFewer);
        }

        [Fact]
        public void Enum_Members_And_Flags_Equality()
        {
            var a = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: true);
            var aAgain = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: true);
            var reordered = new ValueNode.Enum("Game.Layers", new[] { "Water", "Ground" }, IsFlags: true);
            var notFlags = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: false);
            var differentType = new ValueNode.Enum("Game.Faction", new[] { "Ground", "Water" }, IsFlags: true);

            Assert.Equal(a, aAgain);
            Assert.NotEqual(a, reordered);
            Assert.NotEqual(a, notFlags);
            Assert.NotEqual(a, differentType);
        }

        [Fact]
        public void Unsupported_RawTokenEquality()
        {
            var a = new ValueNode.Unsupported("SomeWeirdExpr()");
            var aAgain = new ValueNode.Unsupported("SomeWeirdExpr()");
            var different = new ValueNode.Unsupported("OtherExpr()");

            Assert.Equal(a, aAgain);
            Assert.NotEqual(a, different);
        }

        [Fact]
        public void EqualNodes_HaveEqualHashCode()
        {
            var x = new ValueNode.Primitive(PrimitiveKind.Int, 1);
            var y = new ValueNode.Primitive(PrimitiveKind.Int, 2);

            var list = new ValueNode.List(new ValueNode[] { x, y });
            var listAgain = new ValueNode.List(new ValueNode[] { x, y });
            Assert.Equal(list.GetHashCode(), listAgain.GetHashCode());

            var map = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("x", x) });
            var mapAgain = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("x", x) });
            var nested = new ValueNode.Nested(map);
            var nestedAgain = new ValueNode.Nested(mapAgain);
            Assert.Equal(nested.GetHashCode(), nestedAgain.GetHashCode());

            var flags = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: true);
            var flagsAgain = new ValueNode.Enum("Game.Layers", new[] { "Ground", "Water" }, IsFlags: true);
            Assert.Equal(flags.GetHashCode(), flagsAgain.GetHashCode());
        }
    }
}
