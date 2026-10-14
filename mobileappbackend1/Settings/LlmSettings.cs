namespace mobileappbackend1.Settings
{
    // Configuration for the LLM used by the plan generator.
    // Secrets live in user-secrets or environment variables, never appsettings.json:
    //   dotnet user-secrets set "Llm:ApiKey" "sk-ant-..."
    //   dotnet user-secrets set "Llm:Model"  "claude-opus-4-7"
    public class LlmSettings
    {
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "claude-opus-4-7";
    }
}
