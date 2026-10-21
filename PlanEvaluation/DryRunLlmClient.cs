using System.Text.Json;
using System.Text.Json.Nodes;
using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Planning.Llm;

namespace PlanEvaluation;

// Deterministic ILlmClient for --dry-run. It never touches the network, so
// the harness can run end-to-end without an API key.
//
// Strategy: when asked for a plan, run ConstraintPlanGenerator against the
// same anonymized profile we would send to a real model, then serialize
// the winner into the exact tool-schema JSON LlmPlanService expects. The
// result is a valid plan on nearly every profile, which exercises the full
// LlmPlanService + PlanRepairService pipeline without any LLM API cost.
//
// Approximate token counts are reported from the prompt length so cost /
// latency columns in the CSV still have a defined value per row.
internal sealed class DryRunLlmClient : ILlmClient
{
    private const double FakeCharsPerToken = 4.0;   // ~GPT-ish rule of thumb
    private const int FakeOutputTokens = 1200;

    private readonly ConstraintPlanGenerator _generator = new();
    private readonly IReadOnlyList<Exercise> _exercises;
    private readonly Random _rng;

    public DryRunLlmClient(IReadOnlyList<Exercise> exercises, int seed)
    {
        _exercises = exercises;
        _rng = new Random(seed);
    }

    public Task<LlmJsonResponse> GenerateJsonAsync(
        string systemPrompt, string userPrompt, LlmToolSchema toolSchema, CancellationToken ct = default)
    {
        var profile = ExtractProfile(userPrompt);
        var plans = _generator.Generate(profile, _exercises, candidateCount: 1, seed: _rng.Next());
        string? toolJson = plans.Count == 0 ? null : SerializeAsToolOutput(plans[0]);

        int inTokens = (int)Math.Ceiling((systemPrompt.Length + userPrompt.Length) / FakeCharsPerToken);
        return Task.FromResult(new LlmJsonResponse
        {
            Json         = toolJson,
            RawText      = toolJson ?? "{\"content\":[{\"type\":\"text\",\"text\":\"dry-run: no plan\"}]}",
            InputTokens  = inTokens,
            OutputTokens = toolJson == null ? 20 : FakeOutputTokens,
        });
    }

    public Task<string> GenerateTextAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
        => Task.FromResult("[dry-run] This plan builds strength through balanced compound work across the week.");

    // Reconstruct an AthleteProfile from the lines the LlmPlanService
    // prompt emits for AppendProfile(). We only need the fields the
    // ConstraintPlanGenerator reads; the rest default sensibly.
    private static AthleteProfile ExtractProfile(string userPrompt)
    {
        string? Field(string label)
        {
            foreach (var raw in userPrompt.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("- " + label + ":", StringComparison.Ordinal))
                    return line[(label.Length + 3)..].Trim();
            }
            return null;
        }

        Enum.TryParse<Goal>(Field("Goal"), true, out var goal);
        Enum.TryParse<ExperienceLevel>(Field("Experience"), true, out var level);
        int days = int.TryParse(Field("Days per week"), out var d) ? d : 3;
        int minutes = int.TryParse(Field("Session minutes"), out var m) ? m : 60;
        var equipment = (Field("Equipment") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<EquipmentType>(s, true, out var e) ? e : (EquipmentType?)null)
            .Where(e => e.HasValue).Select(e => e!.Value).ToList();
        var tags = (Field("Avoid stress tags") ?? "")
            .Replace("(none)", "", StringComparison.OrdinalIgnoreCase)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new AthleteProfile
        {
            AthleteId          = PlanAnonymizer.AnonymousId,
            Goal               = goal,
            ExperienceLevel    = level,
            DaysPerWeek        = days,
            SessionMinutes     = minutes,
            AvailableEquipment = equipment,
            AvoidStressTags    = tags,
        };
    }

    private static string SerializeAsToolOutput(TrainingPlan plan)
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
