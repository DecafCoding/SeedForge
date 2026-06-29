namespace SeedForge.Services.Ai
{
    /// <summary>
    /// How a structured (typed) completion asks the model for JSON. Local OpenAI-compatible servers often reject or
    /// 500 on a strict <c>json_schema</c> response_format, so the default is the most tolerant prompt-based strategy.
    /// </summary>
    public enum StructuredOutputMode
    {
        /// <summary>No <c>response_format</c>: the schema is described in the prompt and the reply is parsed defensively. Most compatible.</summary>
        Prompt = 0,

        /// <summary>Send <c>response_format: { type: "json_object" }</c> plus the schema in the prompt. For servers with basic JSON mode but no schema support.</summary>
        JsonObject = 1,

        /// <summary>Send a strict <c>response_format: { type: "json_schema", strict: true, schema }</c>. Hosted OpenAI and servers with real grammar-constrained decoding.</summary>
        JsonSchema = 2,
    }
}
