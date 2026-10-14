# AI-assisted training plan generator — spec

Thesis goal: a **hybrid** plan generator (deterministic constraint-based core + LLM layer + validator + autoregulation + ML scoring), with a trainer always approving the result, and a measurable evaluation showing hybrid > LLM-only.

```
AthleteProfile ─┐
Trainer prefs ──┼─► ConstraintPlanGenerator ─► candidate plans ─► PlanScoringService (ML) ─► best plan
Exercise DB ────┘            ▲                                                              │
                             │                                                              ▼
Trainer NL request ─► LlmPlanService (structured JSON) ─► PlanValidator ─► repair loop ─► draft plan
                                                                                            │
                                     Trainer edits / accepts ◄──────────────────────────────┘
                                                │
                                     PlanMaterializer ─► Workouts ─► athlete logs (RPE)
                                                                         │
                                     AutoregulationService ◄─────────────┘ (next-week loads)
```

---

## Phase 0 — Prerequisites (existing gaps)

1. **Test project**: create `mobileappbackend1.Tests` (xUnit), add to the .sln, add a smoke test.
2. **Exercise metadata** — extend `Models/Exercise.cs` (keep `MuscleGroup` for backward compat with ML):
   - `List<string> SecondaryMuscleGroups`
   - `MovementPattern` enum: `HorizontalPush, VerticalPush, HorizontalPull, VerticalPull, Squat, Hinge, Lunge, Carry, Isolation, Core`
   - `bool IsCompound`
   - `ExperienceLevel MinLevel` enum: `Beginner, Intermediate, Advanced`
   - `List<EquipmentType> Equipment` enum: `Barbell, Dumbbell, Machine, Cable, Bodyweight, Kettlebell, Band` (migrate from the free-text string)
   - `List<string> StressTags` — joints/regions loaded, e.g. `knee`, `lower_back`, `shoulder`, `wrist`, `elbow`
   - Update the seed in `Program.cs`: fill these fields for all existing exercises; expand the library to ~80 (at least 2 options per movement pattern per equipment type where sensible). Seed must be idempotent (upsert by Name).
3. **Structured athlete profile** — new `Models/AthleteProfile.cs` (collection `AthleteProfiles`, unique index on AthleteId). The existing OnboardingForm stays (trainer custom questions); this is the machine-readable part:
   - `Goal` enum: `Hypertrophy, Strength, GeneralFitness, FatLoss`
   - `ExperienceLevel`, `int DaysPerWeek` (2–6), `int SessionMinutes` (30–120)
   - `List<EquipmentType> AvailableEquipment`
   - `List<string> AvoidStressTags` (e.g. `knee`), `List<string> ExcludedExerciseIds`
   - `Dictionary<string,double> MuscleGroupPriority` (0.5–1.5, default 1.0)
   - Also `double? WeightKg`, `string? SportHistory` (the Flutter register survey already collects these + goal, injuries, frequency, equipment — today it only saves them locally; stress tags it maps to: `knee, shoulder, lower_back, ankle, wrist, elbow, hip, neck`).
   - Endpoints: `GET/PUT /api/athleteprofile/{athleteId}` (trainer of that athlete) and `GET/PUT /api/athleteprofile/me` (athlete).
4. **Packages**: `Google.OrTools` (CP-SAT), `Anthropic` official .NET SDK (or `Microsoft.Extensions.AI` with a provider) — wrap behind `ILlmClient` so the model is swappable. API key via user-secrets: `Llm:ApiKey`, `Llm:Model` in `LlmSettings`.

## Phase 1 — Plan domain model

New `Models/TrainingPlan.cs` (collection `TrainingPlans`):
- `TrainingPlan`: Id, TrainerId, AthleteId, TrainingBlockId?, Title, Goal, `int Weeks`, `SplitType` enum (`FullBody, UpperLower, PushPullLegs, Custom`), `PlanStatus` enum (`Draft, Accepted, Materialized, Archived`), `List<PlanDay> Days` (one microcycle template), `ProgressionScheme` (enum: `Linear, DoubleProgression, RpeBased`), `GenerationSource` enum (`Constraint, LlmOnly, Hybrid, Manual`), CreatedAt, AcceptedAt
- `PlanDay`: DayIndex, Label (e.g. "Upper A"), `List<PlannedExercise> Exercises`
- `PlannedExercise`: ExerciseId, Name, MuscleGroup (denormalized), Sets, RepMin, RepMax, TargetRpe, RestSeconds, Order, Notes
- `PlanGenerationLog` (collection `PlanGenerationLogs`) — the evaluation data source: PlanId, Mode, input profile snapshot, raw LLM output(s), violations per attempt, repair attempt count, final plan snapshot, trainer-final snapshot (on accept), latency ms, input/output tokens, predicted progress score.

## Phase 2 — Volume landmarks + PlanValidator (pure logic, test-first)

- `Planning/VolumeLandmarks.cs`: weekly hard-set targets per muscle group per experience level (MEV / MAV / MRV). Start from RP-style values, keep them in one static table with a source comment.
- `Planning/PlanValidator.cs`: `IReadOnlyList<Violation> Validate(TrainingPlan plan, AthleteProfile profile, IReadOnlyDictionary<string, Exercise> exercises)`
- `Violation { Code, Severity (Error|Warning), Message, DayIndex?, ExerciseId? }`. Rules (each its own small method, each with unit tests):
  - `UNKNOWN_EXERCISE` exercise id not in DB
  - `EQUIPMENT_UNAVAILABLE`
  - `STRESS_TAG_CONFLICT` exercise loads an avoided region
  - `EXCLUDED_EXERCISE`
  - `LEVEL_TOO_HIGH` exercise MinLevel > athlete level
  - `VOLUME_BELOW_MEV` / `VOLUME_ABOVE_MRV` per muscle group (weekly sets, secondary muscles count 0.5)
  - `SESSION_TOO_LONG` estimated minutes (sets × (work + rest)) > SessionMinutes
  - `DAY_COUNT_MISMATCH` Days.Count ≠ DaysPerWeek
  - `FREQUENCY_TOO_LOW` muscle group trained < 2×/week (warning, hypertrophy goal)
  - `INVALID_PRESCRIPTION` RepMin > RepMax, sets ∉ 1..10, RPE ∉ 5..10
  - `PATTERN_IMBALANCE` push:pull weekly set ratio outside 0.67–1.5 (warning)
  - `DUPLICATE_IN_DAY`

## Phase 3 — ConstraintPlanGenerator (CP-SAT)

`Planning/ConstraintPlanGenerator.cs`: `List<TrainingPlan> Generate(AthleteProfile, IReadOnlyList<Exercise>, int candidateCount)`
- Choose split from DaysPerWeek (2–3 FullBody, 4 UpperLower, 5–6 PPL).
- Decision vars: `x[d,e]` exercise e on day d (bool), `s[d,e]` sets (0..5).
- Hard constraints: everything PlanValidator marks as Error must be impossible (equipment, tags, level, exclusions, MEV ≤ weekly sets ≤ MRV, session time, max 1 per exercise per day, 4–8 exercises/day, ≥1 compound per day).
- Objective: maximize Σ priority-weighted volume closeness to MAV midpoint + compound bonus + pattern balance; penalize repeating the same exercise across days.
- Diversity: generate N candidates by adding no-good cuts / random objective noise (seeded).
- Rep ranges / RPE from goal (e.g. Strength 3–6 @ RPE 8; Hypertrophy 6–12 @ RPE 7–9; compounds lower reps than isolation).
- Post-condition (unit test): every generated plan has zero Error violations.

## Phase 4 — PlanScoringService (reuse existing ML)

`Planning/PlanScoringService.cs`: for each candidate plan, build `WeeklyFeatureVector`s per planned exercise (use athlete history via `FeatureEngineeringService` when available, otherwise profile-based defaults, `OverlapScore` from `MuscleOverlap` with the plan's dominant focus), run `PredictionEngineService.Predict`, aggregate predicted 1RM delta weighted by `MuscleGroupPriority`. Return candidates ranked. If the model is not ready, fall back to the CP-SAT objective value.

## Phase 5 — LLM layer + repair loop

`Planning/Llm/ILlmClient.cs`, `AnthropicLlmClient.cs`, `LlmPlanService.cs`:
- Prompt includes: profile, allowed exercise list (id, name, muscles, pattern, equipment — ONLY filtered allowed ones), volume landmarks, JSON schema of `TrainingPlan` days. Output = strict JSON (use structured output / tool-use schema), parsed into `TrainingPlan`.
- Operations:
  1. `GenerateLlmOnly(profile)` — baseline for evaluation (no CP-SAT).
  2. `Refine(plan, instruction)` — trainer natural-language edit ("more shoulder focus", "swap squats, knee pain").
  3. `Explain(plan)` — Hungarian/English explanation for the athlete.
- **Repair loop** `PlanRepairService`: LLM output → `PlanValidator` → if Errors, send violations back to the LLM (max 3 attempts) → if still failing, deterministic fix (swap offending exercise with best valid alternative of same pattern; clamp sets). Log every attempt to `PlanGenerationLog`.
- Privacy: send no names/emails/ids of real users to the LLM — only the anonymized profile.
- Rate-limit the generate/refine endpoints per trainer (ASP.NET rate limiter).

## Phase 6 — API

`Controllers/PlanController.cs` (`[Authorize(Roles="Trainer")]` unless noted, check trainer owns the athlete):
- `POST /api/plan/generate` `{ athleteId, mode: "Hybrid"|"Constraint"|"LlmOnly", weeks, trainingBlockId? }` → draft plan + violations (warnings) + predicted score + candidates summary
- `POST /api/plan/{id}/refine` `{ instruction }` → updated draft + diff summary + violations
- `PUT /api/plan/{id}` manual edit → re-validate, return violations
- `POST /api/plan/{id}/validate`
- `POST /api/plan/{id}/accept` → stores trainer-final snapshot in the log
- `POST /api/plan/{id}/materialize` `{ startDate }` → creates `Workout` documents for each week/day (status Planned, MuscleGroup denormalized, target weights from AutoregulationService or empty)
- `GET /api/plan/{id}/explanation`
- `GET /api/plan/athlete/{athleteId}` (trainer) and `GET /api/plan/me` (athlete, accepted plans only)

## Phase 7 — AutoregulationService

`Planning/AutoregulationService.cs` — pure function over the last logged sessions of an exercise:
- Double progression: all sets hit RepMax at RPE ≤ target → +2.5 kg (upper) / +5 kg (lower), else keep, if RPE ≥ target+1.5 or reps < RepMin → −5%.
- RPE-based: estimate e1RM from (weight, reps, RPE) using an RPE table, prescribe next weight for target reps @ target RPE, round to 2.5 kg.
- Deload trigger: e1RM stagnates/drops 2 consecutive weeks or avg RPE ≥ 9.5 → suggest deload week (−40% volume).
- `GET /api/autoregulation/{athleteId}/next-week` (trainer) → suggestions per exercise with reason string. Used by materialize and the Flutter workout editor.

## Phase 8 — Evaluation harness (thesis chapter data)

New console project `PlanEvaluation`:
- Generate N (e.g. 200) random but realistic `AthleteProfile`s (seeded).
- Run each in modes LlmOnly / Constraint / Hybrid.
- Output CSV: mode, profile id, error violation count by code, warning count, repair attempts, latency, tokens, cost estimate, predicted progress score, volume-per-muscle deviation from MAV.
- Plus summary table (mean ± sd) and a script/notebook to plot.
- Trainer study (manual): export 10 profiles × (generated vs hand-written) anonymized for blind rating 1–5; compute edit distance between generated and trainer-final plans from `PlanGenerationLog`.

## Flutter frontend (separate repo)
1. Athlete profile form (structured fields above) — athlete onboarding + trainer can edit.
2. Trainer: "Generate plan" wizard (mode, weeks, block) → loading → plan preview.
3. Plan editor: days as tabs/cards, exercise rows (sets, rep range, RPE), swap exercise (filtered picker), reorder, live violation badges from `/validate`.
4. Refine box: text field → `/refine`, show changes highlighted.
5. Predicted progress + explanation panel.
6. Accept → materialize with start date → shows created workouts in calendar.
7. Workout editor: autoregulation suggestion chips ("+2.5 kg — all sets RPE 7").
8. Athlete: read-only plan view + explanation.
