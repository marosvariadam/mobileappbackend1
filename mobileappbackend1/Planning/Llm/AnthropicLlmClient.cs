using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using mobileappbackend1.Settings;

namespace mobileappbackend1.Planning.Llm
{
    // Anthropic Messages API implementation of ILlmClient. Uses a bare
    // HttpClient so the dependency surface stays small (System.Text.Json +
    // Microsoft.Extensions.Http) and the JSON shape stays obvious to anyone
    // reading the code - no SDK-specific builder calls to decode.
    //
    // Secrets live in LlmSettings (user-secrets / env), never appsettings.json.
    public class AnthropicLlmClient : ILlmClient
    {
        private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";
        private const int MaxTokens = 4096;

        private readonly HttpClient _http;
        private readonly LlmSettings _settings;

        public AnthropicLlmClient(HttpClient http, IOptions<LlmSettings> settings)
        {
            _http = http;
            _settings = settings.Value;
        }

        public async Task<LlmJsonResponse> GenerateJsonAsync(
            string systemPrompt,
            string userPrompt,
            LlmToolSchema toolSchema,
            CancellationToken ct = default)
        {
            // Force structured output through tool-use with tool_choice set to
            // the specific tool. The model MUST emit a tool_use block that
            // conforms to the schema; nothing else is valid output.
            var inputSchema = JsonNode.Parse(toolSchema.JsonSchema) ?? new JsonObject();
            var payload = new JsonObject
            {
                ["model"]       = _settings.Model,
                ["max_tokens"]  = MaxTokens,
                ["system"]      = systemPrompt,
                ["tools"]       = new JsonArray(new JsonObject
                {
                    ["name"]         = toolSchema.Name,
                    ["description"]  = toolSchema.Description,
                    ["input_schema"] = inputSchema,
                }),
                ["tool_choice"] = new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = toolSchema.Name,
                },
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"]    = "user",
                    ["content"] = userPrompt,
                }),
            };

            using var request = NewRequest(payload);
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            string? toolJson = null;
            if (doc.RootElement.TryGetProperty("content", out var content))
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type)
                        && type.GetString() == "tool_use"
                        && block.TryGetProperty("input", out var input))
                    {
                        toolJson = input.GetRawText();
                        break;
                    }
                }
            }

            var (inTokens, outTokens) = ReadUsage(doc.RootElement);
            return new LlmJsonResponse
            {
                Json         = toolJson,
                RawText      = body,
                InputTokens  = inTokens,
                OutputTokens = outTokens,
            };
        }

        public async Task<string> GenerateTextAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken ct = default)
        {
            var payload = new JsonObject
            {
                ["model"]      = _settings.Model,
                ["max_tokens"] = MaxTokens,
                ["system"]     = systemPrompt,
                ["messages"]   = new JsonArray(new JsonObject
                {
                    ["role"]    = "user",
                    ["content"] = userPrompt,
                }),
            };

            using var request = NewRequest(payload);
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            var builder = new StringBuilder();
            if (doc.RootElement.TryGetProperty("content", out var content))
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type)
                        && type.GetString() == "text"
                        && block.TryGetProperty("text", out var text))
                    {
                        builder.Append(text.GetString());
                    }
                }
            }
            return builder.ToString();
        }

        private HttpRequestMessage NewRequest(JsonObject payload)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException(
                    "Llm:ApiKey not configured. Set via `dotnet user-secrets set Llm:ApiKey <key>`.");

            var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-api-key", _settings.ApiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        private static (int inTokens, int outTokens) ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage)) return (0, 0);
            int inT  = usage.TryGetProperty("input_tokens",  out var i) ? i.GetInt32() : 0;
            int outT = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            return (inT, outT);
        }
    }
}
