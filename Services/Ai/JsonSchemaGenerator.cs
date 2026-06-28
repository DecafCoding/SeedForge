using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace SeedForge.Services.Ai
{
    /// <summary>Generates an OpenAI strict-mode JSON schema from a CLR type via the in-box <see cref="JsonSchemaExporter"/>.</summary>
    public static class JsonSchemaGenerator
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        /// <summary>Builds a strict schema for <typeparamref name="T"/>: every object closed (additionalProperties:false) with all properties required.</summary>
        public static JsonNode ForType<T>()
        {
            var exporterOptions = new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = static (_, node) => MakeStrict(node),
            };

            return SerializerOptions.GetJsonSchemaAsNode(typeof(T), exporterOptions);
        }

        /// <summary>For an object node, forces additionalProperties:false and lists every property in required (OpenAI strict mode).</summary>
        private static JsonNode MakeStrict(JsonNode node)
        {
            if (node is not JsonObject obj)
            {
                return node;
            }

            if (obj["properties"] is JsonObject properties)
            {
                obj["additionalProperties"] = false;

                var required = new JsonArray();
                foreach (var property in properties)
                {
                    required.Add(property.Key);
                }
                obj["required"] = required;
            }

            return obj;
        }
    }
}
