using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading;
using SceneBuilder.Core.Serialization;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    public class CanonicalJsonTests
    {
        private class SamplePoco
        {
            [JsonPropertyOrder(0)]
            public string Name { get; set; } = "";

            [JsonPropertyOrder(1)]
            public double Value { get; set; }

            [JsonPropertyOrder(2)]
            public string? Nested { get; set; }
        }

        [Fact]
        public void CanonicalJson_Serialize_UsesLfNewlines_NoCrLf()
        {
            var poco = new SamplePoco { Name = "a", Value = 1, Nested = "b" };

            var json = CanonicalJson.Serialize(poco);

            Assert.DoesNotContain("\r", json);
            Assert.Contains("\n", json);
        }

        [Fact]
        public void CanonicalJson_Serialize_FormatsNumbersInvariant_UnderNonInvariantCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var poco = new SamplePoco { Name = "a", Value = 1.5, Nested = null };

                var json = CanonicalJson.Serialize(poco);

                Assert.Contains("1.5", json);
                Assert.DoesNotContain("1,5", json);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void CanonicalJson_Serialize_IsByteIdenticalAcrossCalls()
        {
            var poco = new SamplePoco { Name = "a", Value = 1, Nested = "b" };

            var json1 = CanonicalJson.Serialize(poco);
            var json2 = CanonicalJson.Serialize(poco);

            Assert.Equal(json1, json2);
        }

        [Fact]
        public void CanonicalJson_RoundTrips_ArbitrarySmallPoco()
        {
            var poco = new SamplePoco { Name = "a", Value = 1.5, Nested = "b" };

            var json = CanonicalJson.Serialize(poco);
            var result = CanonicalJson.Deserialize<SamplePoco>(json);

            Assert.Equal(poco.Name, result.Name);
            Assert.Equal(poco.Value, result.Value);
            Assert.Equal(poco.Nested, result.Nested);
        }

        [Fact]
        public void CanonicalJson_RespectsPropertyOrder_And_CamelCaseKeys()
        {
            var poco = new SamplePoco { Name = "a", Value = 1, Nested = "b" };

            var json = CanonicalJson.Serialize(poco);

            int nameIndex = json.IndexOf("\"name\"");
            int valueIndex = json.IndexOf("\"value\"");
            int nestedIndex = json.IndexOf("\"nested\"");

            Assert.True(nameIndex >= 0, "expected camelCase \"name\" key");
            Assert.True(valueIndex >= 0, "expected camelCase \"value\" key");
            Assert.True(nestedIndex >= 0, "expected camelCase \"nested\" key");
            Assert.True(nameIndex < valueIndex, "expected declaration order: name before value");
            Assert.True(valueIndex < nestedIndex, "expected declaration order: value before nested");
        }
    }
}
