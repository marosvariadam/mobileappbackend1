using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using mobileappbackend1.Models;

namespace mobileappbackend1.Planning.Llm
{
    /// <summary>
    /// Prompts the LLM for training plans via strict tool-schema JSON and
    /// parses the result into <see cref="TrainingPlan"/>. The service is the
    /// seam between a provider-agnostic <see cref="ILlmClient"/> and the plan
    /// domain model; it owns the prompt, the schema, and the JSON parser.
    /// </summary>
    /// <remarks>
    /// All profile data crossing this boundary is already anonymized by the
    /// caller through <see cref="PlanAnonymizer"/>. The allowed-exercise list
    /// is pre-filtered to the athlete's equipment, level, stress tags, and
    /// exclusions so the model can only reference lifts it is allowed to pick.
    /// </remarks>
    public class LlmPlanService
    {
        private const string ToolName = "emit_training_plan";
        private const string ToolDescription =
            "Emit a strict JSON description of a weekly training plan microcycle. "
            + "You MUST pick exerciseId values only from the allowed exercise list.";

        private readonly ILlmClient _client;

        public LlmPlanService(ILlmClient client)
        {
            _client = client;
        }

        public readonly record struct LlmPlanResult(
            TrainingPlan? Plan,
            string RawOutput,
            int InputTokens,
            int OutputTokens);

        // ── Public ops ──────────────────────────────────────────────────────

        public Task<LlmPlanResult> GenerateLlmOnlyAsync(
            AthleteProfile anonymizedProfile,
            IReadOnlyList<Exercise> allowedExercises,
            CancellationToken ct = default)
        {
            var user = BuildGeneratePrompt(anonymizedProfile, allowedExercises);
            return RunAsync(user, anonymizedProfile, allowedExercises, ct);
        }

        public Task<LlmPlanResult> RefineAsync(
            TrainingPlan plan,
            string instruction,
            AthleteProfile anonymizedProfile,
            IReadOnlyList<Exercise> allowedExercises,
            CancellationToken ct = default)
        {
            var user = BuildRefinePrompt(plan, instruction, anonymizedProfile, allowedExercises);
            return RunAsync(user, anonymizedProfile, allowedExercises, ct);
        }

        public Task<LlmPlanResult> RepairAsync(
            TrainingPlan plan,
            IReadOnlyList<Violation> violations,
            AthleteProfile anonymizedProfile,
            IReadOnlyList<Exercise> allowedExercises,
            CancellationToken ct = default)
        {
            var user = BuildRepairPrompt(plan, violations, anonymizedProfile, allowedExercises);
            return RunAsync(user, anonymizedProfile, allowedExercises, ct);
        }

        public Task<string> ExplainAsync(
            TrainingPlan plan,
            string language,
            CancellationToken ct = default)
        {
            var system =
                "You are a friendly strength coach. Explain training plans to the athlete "
                + "in plain language, 3-6 sentences, no medical claims, no marketing fluff.";
            var user = $"Language: {(string.IsNullOrWhiteSpace(language) ? "English" : language)}\n\n"
                       + "Plan JSON:\n" + SerializePlan(plan);
            return _client.GenerateTextAsync(system, user, ct);
        }

        // ── Core round-trip ─────────────────────────────────────────────────

        private async Task<LlmPlanResult> RunAsync(
            string userPrompt,
            AthleteProfile profile,
            IReadOnlyList<Exercise> allowed,
            CancellationToken ct)
        {
            var schema = new LlmToolSchema
            {
                Name        = ToolName,
                Description = ToolDescription,
                JsonSchema  = PlanJsonSchema,
            };
            var response = await _client.GenerateJsonAsync(SystemPrompt, userPrompt, schema, ct);

            TrainingPlan? plan = null;
            if (!string.IsNullOrWhiteSpace(response.Json))
            {
                try { plan = ParsePlan(response.Json!, profile, allowed); }
                catch (JsonException) { plan = null; }
                catch (InvalidOperationException) { plan = null; }
            }

            return new LlmPlanResult(plan, response.RawText, response.InputTokens, response.OutputTokens);
        }

        // ── Prompts ─────────────────────────────────────────────────────────

        private const string SystemPrompt =
            "You are a strength and conditioning coach. You design evidence-based "
            + "training microcycles. You MUST obey these rules:\n"
            + "- Return your answer ONLY via the emit_training_plan tool.\n"
            + "- Every exerciseId MUST appear in the allowed-exercise list below. "
            + "Do not invent ids or names.\n"
            + "- Honor the athlete's days-per-week, session minutes, equipment, "
            + "excluded exercises, stress-tag avoidance, and experience level.\n"
            + "- Respect weekly volume landmarks (MEV <= weekly sets <= MRV per muscle group).\n"
            + "- 4-8 exercises per day, at least one compound per day, no duplicate "
            + "exercise within one day, repMin <= repMax, 1 <= sets <= 10, 5 <= RPE <= 10.";

        private static string BuildGeneratePrompt(AthleteProfile profile, IReadOnlyList<Exercise> allowed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Athlete profile (anonymized)");
            AppendProfile(sb, profile);
            sb.AppendLine();
            sb.AppendLine("## Volume landmarks (weekly sets by muscle group)");
            AppendLandmarks(sb, profile.ExperienceLevel);
            sb.AppendLine();
            sb.AppendLine("## Allowed exercises (pre-filtered)");
            AppendAllowedExercises(sb, allowed);
            sb.AppendLine();
            sb.AppendLine("Design one weekly microcycle. Pick the split that fits the days-per-week.");
            return sb.ToString();
        }

        private static string BuildRefinePrompt(
            TrainingPlan plan, string instruction,
            AthleteProfile profile, IReadOnlyList<Exercise> allowed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Athlete profile (anonymized)");
            AppendProfile(sb, profile);
            sb.AppendLine();
            sb.AppendLine("## Current plan");
            sb.AppendLine(SerializePlan(plan));
            sb.AppendLine();
            sb.AppendLine("## Trainer instruction");
            sb.AppendLine(instruction);
            sb.AppendLine();
            sb.AppendLine("## Allowed exercises (pre-filtered)");
            AppendAllowedExercises(sb, allowed);
            sb.AppendLine();
            sb.AppendLine("Return the FULL refined plan via the tool, not a diff.");
            return sb.ToString();
        }

        private static string BuildRepairPrompt(
            TrainingPlan plan, IReadOnlyList<Violation> violations,
            AthleteProfile profile, IReadOnlyList<Exercise> allowed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Previous plan had validation errors - repair it.");
            sb.AppendLine();
            sb.AppendLine("## Athlete profile (anonymized)");
            AppendProfile(sb, profile);
            sb.AppendLine();
            sb.AppendLine("## Previous plan");
            sb.AppendLine(SerializePlan(plan));
            sb.AppendLine();
            sb.AppendLine("## Validator errors to fix");
            foreach (var v in violations)
            {
                sb.Append("- [").Append(v.Code).Append("] ").Append(v.Message);
                if (v.DayIndex.HasValue) sb.Append(" (day ").Append(v.DayIndex).Append(')');
                if (!string.IsNullOrEmpty(v.ExerciseId)) sb.Append(" (exerciseId ").Append(v.ExerciseId).Append(')');
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.AppendLine("## Allowed exercises (pre-filtered)");
            AppendAllowedExercises(sb, allowed);
            sb.AppendLine();
            sb.AppendLine("Return a REPAIRED full plan via the tool. Fix only what the validator flagged.");
            return sb.ToString();
        }

        private static void AppendProfile(StringBuilder sb, AthleteProfile profile)
        {
            sb.Append("- Goal: ").AppendLine(profile.Goal.ToString());
            sb.Append("- Experience: ").AppendLine(profile.ExperienceLevel.ToString());
            sb.Append("- Days per week: ").Append(profile.DaysPerWeek).AppendLine();
            sb.Append("- Session minutes: ").Append(profile.SessionMinutes).AppendLine();
            sb.Append("- Equipment: ")
              .AppendLine(string.Join(", ", profile.AvailableEquipment));
            sb.Append("- Avoid stress tags: ")
              .AppendLine(profile.AvoidStressTags.Count == 0 ? "(none)" : string.Join(", ", profile.AvoidStressTags));
            if (profile.WeightKg.HasValue)
                sb.Append("- Bodyweight kg: ").Append(profile.WeightKg.Value.ToString("0.#")).AppendLine();
            if (profile.MuscleGroupPriority.Count > 0)
            {
                sb.Append("- Muscle priorities: ")
                  .AppendLine(string.Join(", ", profile.MuscleGroupPriority
                      .Select(kv => $"{kv.Key}={kv.Value:0.0}")));
            }
        }

        private static void AppendLandmarks(StringBuilder sb, ExperienceLevel level)
        {
            foreach (var muscle in new[] { "Chest", "Back", "Shoulders", "Legs", "Arms", "Core" })
            {
                var r = VolumeLandmarks.For(muscle, level);
                sb.Append("- ").Append(muscle).Append(": MEV ").Append(r.Mev)
                  .Append(", MAV ").Append(r.Mav).Append(", MRV ").Append(r.Mrv).AppendLine();
            }
        }

        private static void AppendAllowedExercises(StringBuilder sb, IReadOnlyList<Exercise> allowed)
        {
            foreach (var e in allowed)
            {
                sb.Append("- id=").Append(e.Id)
                  .Append(" | ").Append(e.Name)
                  .Append(" | muscle=").Append(e.MuscleGroup);
                if (e.SecondaryMuscleGroups.Count > 0)
                    sb.Append(" (+").Append(string.Join(",", e.SecondaryMuscleGroups)).Append(')');
                sb.Append(" | pattern=").Append(e.MovementPattern)
                  .Append(" | compound=").Append(e.IsCompound ? "yes" : "no")
                  .Append(" | equipment=").Append(string.Join(",", e.Equipment))
                  .AppendLine();
            }
        }

        // ── Schema + parser ─────────────────────────────────────────────────

        // JSON schema the LLM MUST produce. Matches TrainingPlan.Days shape;
        // Name/MuscleGroup are resolved server-side from the exerciseId so the
        // model can't lie about them.
        private const string PlanJsonSchema = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["title", "split", "weeks", "days"],
              "properties": {
                "title":  { "type": "string" },
                "split":  { "type": "string", "enum": ["FullBody","UpperLower","PushPullLegs","Custom"] },
                "weeks":  { "type": "integer", "minimum": 1, "maximum": 52 },
                "days": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["dayIndex","label","exercises"],
                    "properties": {
                      "dayIndex":  { "type": "integer", "minimum": 0 },
                      "label":     { "type": "string" },
                      "exercises": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["exerciseId","sets","repMin","repMax","targetRpe","restSeconds"],
                          "properties": {
                            "exerciseId":  { "type": "string" },
                            "sets":        { "type": "integer", "minimum": 1, "maximum": 10 },
                            "repMin":      { "type": "integer", "minimum": 1, "maximum": 50 },
                            "repMax":      { "type": "integer", "minimum": 1, "maximum": 50 },
                            "targetRpe":   { "type": "integer", "minimum": 5, "maximum": 10 },
                            "restSeconds": { "type": "integer", "minimum": 0, "maximum": 600 }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        private static TrainingPlan ParsePlan(
            string json,
            AthleteProfile profile,
            IReadOnlyList<Exercise> allowed)
        {
            var root = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidOperationException("Root JSON is not an object.");

            var byId = allowed.Where(e => !string.IsNullOrEmpty(e.Id))
                              .ToDictionary(e => e.Id!, e => e);

            var splitStr = (string?)root["split"] ?? "FullBody";
            var split = Enum.TryParse<SplitType>(splitStr, true, out var s) ? s : SplitType.FullBody;

            var plan = new TrainingPlan
            {
                AthleteId   = profile.AthleteId,
                Title       = (string?)root["title"] ?? "Generated plan",
                Goal        = profile.Goal,
                Weeks       = (int?)root["weeks"] ?? 4,
                Split       = split,
                Status      = PlanStatus.Draft,
                Progression = ProgressionFor(profile.Goal),
                Source      = GenerationSource.LlmOnly,
            };

            var days = root["days"] as JsonArray
                       ?? throw new InvalidOperationException("Missing days array.");
            foreach (var dayNode in days.OfType<JsonObject>())
            {
                var day = new PlanDay
                {
                    DayIndex = (int?)dayNode["dayIndex"] ?? plan.Days.Count,
                    Label    = (string?)dayNode["label"] ?? $"Day {plan.Days.Count + 1}",
                };

                var exNodes = dayNode["exercises"] as JsonArray ?? new JsonArray();
                int order = 0;
                foreach (var exNode in exNodes.OfType<JsonObject>())
                {
                    var id = (string?)exNode["exerciseId"];
                    if (string.IsNullOrEmpty(id)) continue;

                    // Resolve name/muscle server-side so the model can't rename
                    // a lift to something it isn't. Unknown ids are preserved
                    // (as UNKNOWN_EXERCISE) so the validator can flag them.
                    byId.TryGetValue(id, out var ex);
                    day.Exercises.Add(new PlannedExercise
                    {
                        ExerciseId  = id,
                        Name        = ex?.Name ?? id,
                        MuscleGroup = ex?.MuscleGroup ?? string.Empty,
                        Sets        = (int?)exNode["sets"] ?? 0,
                        RepMin      = (int?)exNode["repMin"] ?? 0,
                        RepMax      = (int?)exNode["repMax"] ?? 0,
                        TargetRpe   = (int?)exNode["targetRpe"] ?? 0,
                        RestSeconds = (int?)exNode["restSeconds"] ?? 0,
                        Order       = order++,
                    });
                }

                plan.Days.Add(day);
            }

            return plan;
        }

        private static ProgressionScheme ProgressionFor(Goal goal) => goal switch
        {
            Goal.Strength    => ProgressionScheme.Linear,
            Goal.Hypertrophy => ProgressionScheme.DoubleProgression,
            _                => ProgressionScheme.RpeBased,
        };

        private static string SerializePlan(TrainingPlan plan)
        {
            // Compact representation used in refine / repair prompts and in
            // the explain prompt. Keeps only the fields the LLM should see.
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
                        ["name"]        = e.Name,
                        ["muscleGroup"] = e.MuscleGroup,
                        ["sets"]        = e.Sets,
                        ["repMin"]      = e.RepMin,
                        ["repMax"]      = e.RepMax,
                        ["targetRpe"]   = e.TargetRpe,
                        ["restSeconds"] = e.RestSeconds,
                    }).ToArray()),
                }).ToArray()),
            };
            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
