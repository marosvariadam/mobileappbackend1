using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Microsoft.Extensions.Options;
using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Planning.Llm;
using mobileappbackend1.Services;
using mobileappbackend1.Settings;
using PlanEvaluation;

// Phase 8 — evaluation harness. Runs each seeded profile through
// LlmOnly / Constraint / Hybrid, records a per-row metric vector to
// results.csv, and writes a per-mode (mean, sd) rollup to summary.csv.
//
// Usage:
//   dotnet run --project PlanEvaluation -- [--count N] [--seed S]
//                                          [--out DIR] [--dry-run]
//
//   --dry-run             use the in-process DryRunLlmClient (no API key).
//   --count N             number of profiles (default 200).
//   --seed  S             base seed (default 42).
//   --out DIR             output directory (default "eval-output").
//   --api-key KEY         Anthropic API key when not dry-run (else env var
//                         ANTHROPIC_API_KEY or user-secrets Llm:ApiKey).

var opts = CliOptions.Parse(args);

Directory.CreateDirectory(opts.OutputDir);
var resultsPath = Path.Combine(opts.OutputDir, "results.csv");
var summaryPath = Path.Combine(opts.OutputDir, "summary.csv");

Console.WriteLine($"PlanEvaluation: {opts.Count} profiles × 3 modes "
                  + $"→ {resultsPath} / {summaryPath}  (dry-run={opts.DryRun})");

var exercises = SeedExercises();
var scorer    = BuildScorer();
var repair    = BuildRepair(exercises, opts);
var constraint = new ConstraintPlanGenerator();

using var resultsWriter = new StreamWriter(resultsPath);
resultsWriter.WriteLine(CsvHeader());

var rows = new List<EvaluationRow>(opts.Count * 3);
var sw = Stopwatch.StartNew();

for (int i = 0; i < opts.Count; i++)
{
    var profile = ProfileFactory.Build(opts.Seed + i);
    foreach (var mode in new[] { GenerationSource.Constraint, GenerationSource.LlmOnly, GenerationSource.Hybrid })
    {
        var row = await RunOne(profile, mode, exercises, constraint, scorer, repair);
        rows.Add(row);
        resultsWriter.WriteLine(row.ToCsv());
    }
    if ((i + 1) % 10 == 0)
        Console.WriteLine($"  {i + 1}/{opts.Count} profiles done ({sw.Elapsed.TotalSeconds:0.0}s)");
}

resultsWriter.Flush();

WriteSummary(summaryPath, rows);
Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:0.0}s. "
                  + $"Rows: {rows.Count}. See {summaryPath} for the roll-up.");
return 0;


// ── Core run loop ──────────────────────────────────────────────────────────

static async Task<EvaluationRow> RunOne(
    AthleteProfile profile,
    GenerationSource mode,
    IReadOnlyList<Exercise> exercises,
    ConstraintPlanGenerator constraint,
    PlanScoringService scorer,
    PlanRepairService repair)
{
    var sw = Stopwatch.StartNew();
    TrainingPlan? plan = null;
    int inputTokens = 0, outputTokens = 0, repairAttempts = 0;
    bool usedFix = false;

    try
    {
        var candidates = new List<TrainingPlan>();
        if (mode is GenerationSource.Constraint or GenerationSource.Hybrid)
            candidates.AddRange(constraint.Generate(profile, exercises, candidateCount: 2, seed: 1));

        if (mode is GenerationSource.LlmOnly or GenerationSource.Hybrid)
        {
            var r = await repair.GenerateAndRepairAsync(profile, exercises);
            inputTokens  = r.Log.InputTokens;
            outputTokens = r.Log.OutputTokens;
            repairAttempts = r.Log.RepairAttemptCount;
            usedFix = r.UsedDeterministicFix;
            if (r.Plan != null) candidates.Add(r.Plan);
        }

        if (candidates.Count > 0)
        {
            var scored = await scorer.ScoreAsync(candidates, profile);
            plan = scored[0].Plan;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ! {profile.AthleteId} {mode}: {ex.Message}");
    }

    sw.Stop();

    var row = new EvaluationRow
    {
        Mode               = mode.ToString(),
        ProfileId          = profile.AthleteId,
        Goal               = profile.Goal.ToString(),
        ExperienceLevel    = profile.ExperienceLevel.ToString(),
        DaysPerWeek        = profile.DaysPerWeek,
        SessionMinutes     = profile.SessionMinutes,
        LatencyMs          = sw.ElapsedMilliseconds,
        InputTokens        = inputTokens,
        OutputTokens       = outputTokens,
        CostUsd            = EstimateCostUsd(inputTokens, outputTokens),
        RepairAttempts     = repairAttempts,
        UsedDeterministic  = usedFix,
    };

    if (plan == null)
    {
        row.ProduceFailure = true;
        return row;
    }

    var library = exercises.Where(e => !string.IsNullOrEmpty(e.Id))
                           .ToDictionary(e => e.Id!);
    var violations = PlanValidator.Validate(plan, profile, library);

    foreach (var v in violations.Where(v => v.Severity == Severity.Error))
        row.ErrorsByCode[v.Code] = row.ErrorsByCode.GetValueOrDefault(v.Code) + 1;
    row.ErrorCount   = violations.Count(v => v.Severity == Severity.Error);
    row.WarningCount = violations.Count(v => v.Severity == Severity.Warning);
    row.MavDeviation = MavDeviation(plan, profile, library);
    return row;
}


// ── Dependencies ───────────────────────────────────────────────────────────

static IReadOnlyList<Exercise> SeedExercises()
{
    // Clone the canonical seed and assign synthetic ids so plan rows have
    // stable keys. Mongo would do this on insert in prod; here we fake it.
    var list = new List<Exercise>(ExerciseSeed.Canonical.Count);
    for (int i = 0; i < ExerciseSeed.Canonical.Count; i++)
    {
        var src = ExerciseSeed.Canonical[i];
        list.Add(new Exercise
        {
            Id                    = $"ex-{i:D5}",
            Name                  = src.Name,
            MuscleGroup           = src.MuscleGroup,
            SecondaryMuscleGroups = src.SecondaryMuscleGroups.ToList(),
            MovementPattern       = src.MovementPattern,
            IsCompound            = src.IsCompound,
            MinLevel              = src.MinLevel,
            Equipment             = src.Equipment.ToList(),
            StressTags            = src.StressTags.ToList(),
        });
    }
    return list;
}

static PlanScoringService BuildScorer()
{
    // No trained ML model in this harness - the scorer falls back to its
    // deterministic MuscleOverlap × priority proxy, which is what we want
    // for an evaluation that compares plans structurally, not predictively.
    return new PlanScoringService(new NullPredictor());
}

static PlanRepairService BuildRepair(IReadOnlyList<Exercise> exercises, CliOptions opts)
{
    ILlmClient client = opts.DryRun
        ? new DryRunLlmClient(exercises, seed: opts.Seed)
        : BuildAnthropicClient(opts);
    return new PlanRepairService(new LlmPlanService(client));
}

static ILlmClient BuildAnthropicClient(CliOptions opts)
{
    var apiKey = opts.ApiKey
                 ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                 ?? throw new InvalidOperationException(
                        "No API key. Pass --dry-run, --api-key, or set ANTHROPIC_API_KEY.");
    var settings = Options.Create(new LlmSettings { ApiKey = apiKey });
    return new AnthropicLlmClient(new HttpClient(), settings);
}

static double EstimateCostUsd(int inputTokens, int outputTokens)
{
    // Anthropic Claude Opus 4-tier public pricing as of writing: $15 /
    // million input, $75 / million output. The harness isn't tied to this
    // model - edit here if you benchmark a different one.
    const double inUsdPerToken  = 15.0 / 1_000_000.0;
    const double outUsdPerToken = 75.0 / 1_000_000.0;
    return Math.Round(inputTokens * inUsdPerToken + outputTokens * outUsdPerToken, 6);
}

static double MavDeviation(TrainingPlan plan, AthleteProfile profile,
                           IReadOnlyDictionary<string, Exercise> library)
{
    // Weekly sets per muscle group, matching the validator's accounting
    // (primary 1.0, secondary 0.5). Then sum |V_m - MAV_m| across the six
    // seeded muscle groups.
    var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    void Add(string m, double s)
    {
        totals.TryGetValue(m, out var c);
        totals[m] = c + s;
    }
    foreach (var day in plan.Days)
        foreach (var pe in day.Exercises)
        {
            if (!string.IsNullOrEmpty(pe.MuscleGroup)) Add(pe.MuscleGroup, pe.Sets);
            if (!string.IsNullOrEmpty(pe.ExerciseId) && library.TryGetValue(pe.ExerciseId, out var ex))
                foreach (var sm in ex.SecondaryMuscleGroups) Add(sm, pe.Sets * 0.5);
        }

    double deviation = 0;
    foreach (var m in new[] { "Chest", "Back", "Shoulders", "Legs", "Arms", "Core" })
    {
        var range = VolumeLandmarks.For(m, profile.ExperienceLevel);
        totals.TryGetValue(m, out var sets);
        deviation += Math.Abs(sets - range.Mav);
    }
    return deviation;
}


// ── CSV ────────────────────────────────────────────────────────────────────

static string CsvHeader() =>
    "mode,profile_id,goal,level,days_per_week,session_minutes," +
    "produce_failure,error_count,warning_count,repair_attempts,used_deterministic_fix," +
    "latency_ms,input_tokens,output_tokens,cost_usd,mav_deviation," +
    "errors_by_code";

static void WriteSummary(string path, List<EvaluationRow> rows)
{
    using var w = new StreamWriter(path);
    w.WriteLine("mode,n,error_count_mean,error_count_sd,warning_count_mean,"
                + "repair_attempts_mean,produce_failure_rate,"
                + "latency_ms_mean,latency_ms_sd,"
                + "input_tokens_mean,output_tokens_mean,cost_usd_mean,"
                + "mav_deviation_mean,mav_deviation_sd");

    foreach (var group in rows.GroupBy(r => r.Mode))
    {
        var list   = group.ToList();
        var errs   = list.Select(r => (double)r.ErrorCount).ToList();
        var warns  = list.Select(r => (double)r.WarningCount).ToList();
        var reps   = list.Select(r => (double)r.RepairAttempts).ToList();
        var fail   = list.Select(r => r.ProduceFailure ? 1.0 : 0.0).ToList();
        var lat    = list.Select(r => (double)r.LatencyMs).ToList();
        var inTok  = list.Select(r => (double)r.InputTokens).ToList();
        var outTok = list.Select(r => (double)r.OutputTokens).ToList();
        var cost   = list.Select(r => r.CostUsd).ToList();
        var mav    = list.Select(r => r.MavDeviation).ToList();

        var inv = CultureInfo.InvariantCulture;
        w.WriteLine(string.Join(',', new[]
        {
            group.Key,
            list.Count.ToString(inv),
            Mean(errs).ToString("0.###", inv),
            Sd(errs).ToString("0.###", inv),
            Mean(warns).ToString("0.###", inv),
            Mean(reps).ToString("0.###", inv),
            Mean(fail).ToString("0.###", inv),
            Mean(lat).ToString("0.##", inv),
            Sd(lat).ToString("0.##", inv),
            Mean(inTok).ToString("0.#", inv),
            Mean(outTok).ToString("0.#", inv),
            Mean(cost).ToString("0.######", inv),
            Mean(mav).ToString("0.###", inv),
            Sd(mav).ToString("0.###", inv),
        }));
    }
}

static double Mean(IReadOnlyList<double> xs) => xs.Count == 0 ? 0 : xs.Average();
static double Sd(IReadOnlyList<double> xs)
{
    if (xs.Count < 2) return 0;
    var m = xs.Average();
    var sum = xs.Sum(x => (x - m) * (x - m));
    return Math.Sqrt(sum / (xs.Count - 1));
}


// ── Supporting types (same file so there's nothing extra to open) ──────────

internal sealed class NullPredictor : IProgressPredictor
{
    public bool IsReady => false;
    public ProgressPrediction? Predict(ProgressInput input) => null;
}

internal sealed class EvaluationRow
{
    public string Mode = string.Empty;
    public string ProfileId = string.Empty;
    public string Goal = string.Empty;
    public string ExperienceLevel = string.Empty;
    public int DaysPerWeek;
    public int SessionMinutes;
    public bool ProduceFailure;
    public int ErrorCount;
    public int WarningCount;
    public int RepairAttempts;
    public bool UsedDeterministic;
    public long LatencyMs;
    public int InputTokens;
    public int OutputTokens;
    public double CostUsd;
    public double MavDeviation;
    public Dictionary<string, int> ErrorsByCode = new();

    public string ToCsv()
    {
        var inv = CultureInfo.InvariantCulture;
        var errMap = string.Join('|',
            ErrorsByCode.OrderBy(k => k.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
        return string.Join(',', new[]
        {
            Mode,
            ProfileId,
            Goal,
            ExperienceLevel,
            DaysPerWeek.ToString(inv),
            SessionMinutes.ToString(inv),
            ProduceFailure ? "1" : "0",
            ErrorCount.ToString(inv),
            WarningCount.ToString(inv),
            RepairAttempts.ToString(inv),
            UsedDeterministic ? "1" : "0",
            LatencyMs.ToString(inv),
            InputTokens.ToString(inv),
            OutputTokens.ToString(inv),
            CostUsd.ToString("0.######", inv),
            MavDeviation.ToString("0.##", inv),
            errMap,
        });
    }
}

internal sealed class CliOptions
{
    public int Count { get; set; } = 200;
    public int Seed  { get; set; } = 42;
    public string OutputDir { get; set; } = "eval-output";
    public bool DryRun { get; set; }
    public string? ApiKey { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count":   o.Count     = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed":    o.Seed      = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out":     o.OutputDir = args[++i]; break;
                case "--api-key": o.ApiKey    = args[++i]; break;
                case "--dry-run": o.DryRun    = true; break;
                case "-h":
                case "--help":
                    Console.WriteLine("Usage: PlanEvaluation [--count N] [--seed S] [--out DIR] "
                                      + "[--dry-run] [--api-key KEY]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Try --help.");
            }
        }
        return o;
    }
}
