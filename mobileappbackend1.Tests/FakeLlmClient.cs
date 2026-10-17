using System.Text.Json;
using System.Text.Json.Nodes;
using mobileappbackend1.Models;
using mobileappbackend1.Planning.Llm;

namespace mobileappbackend1.Tests
{
    // Scripted ILlmClient for tests - no network calls. Each GenerateJsonAsync
    // call consumes the next entry from the queue; passing the queue dry
    // throws so a test can't silently rely on behavior it didn't script.
    //
    // Captures every prompt it sees so tests can assert on anonymization /
    // allowed-exercise list contents.
    internal sealed class FakeLlmClient : ILlmClient
    {
        private readonly Queue<LlmJsonResponse> _jsonResponses;
        private readonly Queue<string> _textResponses;

        public List<(string System, string User, LlmToolSchema Schema)> JsonCalls { get; } = new();
        public List<(string System, string User)> TextCalls { get; } = new();

        public FakeLlmClient(IEnumerable<LlmJsonResponse> jsonResponses, IEnumerable<string>? textResponses = null)
        {
            _jsonResponses = new Queue<LlmJsonResponse>(jsonResponses);
            _textResponses = new Queue<string>(textResponses ?? Array.Empty<string>());
        }

        public Task<LlmJsonResponse> GenerateJsonAsync(
            string systemPrompt, string userPrompt, LlmToolSchema toolSchema, CancellationToken ct = default)
        {
            JsonCalls.Add((systemPrompt, userPrompt, toolSchema));
            if (_jsonResponses.Count == 0)
                throw new InvalidOperationException("FakeLlmClient out of scripted JSON responses.");
            return Task.FromResult(_jsonResponses.Dequeue());
        }

        public Task<string> GenerateTextAsync(
            string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            TextCalls.Add((systemPrompt, userPrompt));
            if (_textResponses.Count == 0)
                throw new InvalidOperationException("FakeLlmClient out of scripted text responses.");
            return Task.FromResult(_textResponses.Dequeue());
        }

        // ── Fixture builders ────────────────────────────────────────────────

        public static LlmJsonResponse Ok(string json, int inTokens = 100, int outTokens = 200) => new()
        {
            Json         = json,
            RawText      = $"{{\"usage\":{{\"input_tokens\":{inTokens},\"output_tokens\":{outTokens}}}}}",
            InputTokens  = inTokens,
            OutputTokens = outTokens,
        };

        public static LlmJsonResponse NoToolCall() => new()
        {
            Json         = null,
            RawText      = "{\"content\":[{\"type\":\"text\",\"text\":\"I cannot comply.\"}]}",
            InputTokens  = 50,
            OutputTokens = 10,
        };

        // Serialize a TrainingPlan into the exact JSON shape the tool schema
        // expects, so a test can hand the fake a "known-valid" response.
        public static string ToToolJson(TrainingPlan plan)
        {
            var obj = new JsonObject
            {
                ["title"] = plan.Title,
                ["split"] = plan.Split.ToString(),
                ["weeks"] = plan.Weeks,
                ["days"]  = new JsonArray(plan.Days.Select(d => (JsonNode)new JsonObject
                {
                    ["dayIndex"]  = d.DayIndex,
                    ["label"]     = d.Label,
                    ["exercises"] = new JsonArray(d.Exercises.Select(e => (JsonNode)new JsonObject
                    {
                        ["exerciseId"]  = e.ExerciseId,
                        ["sets"]        = e.Sets,
                        ["repMin"]      = e.RepMin,
                        ["repMax"]      = e.RepMax,
                        ["targetRpe"]   = e.TargetRpe,
                        ["restSeconds"] = e.RestSeconds,
                    }).ToArray()),
                }).ToArray()),
            };
            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
    }
}
