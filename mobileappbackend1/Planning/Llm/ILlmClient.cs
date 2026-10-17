namespace mobileappbackend1.Planning.Llm
{
    // Abstraction over the LLM provider so the model is swappable (Anthropic
    // today, something else tomorrow) and so tests can plug in a scripted
    // fake. Everything the plan generator asks of an LLM goes through this
    // interface - no caller imports a provider SDK directly.
    public interface ILlmClient
    {
        /// <summary>
        /// Ask the model for a response that must conform to
        /// <paramref name="toolSchema"/>. The provider is expected to force
        /// the output through its structured-output / tool-use channel so the
        /// returned <see cref="LlmJsonResponse.Json"/> is strict JSON.
        /// </summary>
        Task<LlmJsonResponse> GenerateJsonAsync(
            string systemPrompt,
            string userPrompt,
            LlmToolSchema toolSchema,
            CancellationToken ct = default);

        /// <summary>Free-form text response, used for <c>Explain</c>.</summary>
        Task<string> GenerateTextAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken ct = default);
    }

    // Tool-use schema the provider will enforce on the output. JsonSchema is a
    // raw JSON Schema document; the caller owns the shape so this interface
    // stays provider-agnostic.
    public class LlmToolSchema
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string JsonSchema { get; set; } = "{}";
    }

    public class LlmJsonResponse
    {
        // JSON matching the tool schema, or null if the model refused or an
        // IO error occurred. RawText still carries whatever the provider
        // returned so the caller can log it.
        public string? Json { get; set; }
        public string RawText { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}
