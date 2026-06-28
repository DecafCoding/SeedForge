using System.Text.Json.Nodes;
using SeedForge.Services.Ai;

namespace SeedForge.UnitTests
{
    /// <summary>Proves generated schemas satisfy OpenAI strict mode (closed objects, all properties required).</summary>
    public class JsonSchemaGeneratorTests
    {
        private sealed record Foo(string Title, int Count);

        [Fact]
        public void ForType_closes_objects_and_requires_all_properties()
        {
            var node = JsonSchemaGenerator.ForType<Foo>().AsObject();

            Assert.False(node["additionalProperties"]!.GetValue<bool>());

            var required = node["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var properties = node["properties"]!.AsObject().Select(p => p.Key).ToList();
            // Every declared property must appear in `required` (OpenAI strict mode), whatever the naming policy.
            Assert.Equal(properties.OrderBy(p => p), required.OrderBy(p => p));
            Assert.Equal(2, required.Count);
        }
    }
}
