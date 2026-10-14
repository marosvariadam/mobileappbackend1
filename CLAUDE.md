# CLAUDE.md — mobileappbackend1

Backend of a B2B fitness coaching app (thesis project). Trainers manage athletes, assign workouts and training blocks; athletes log sessions. A Flutter app is the client.

## Stack
- .NET 8, ASP.NET Core Web API, C#, nullable enabled
- MongoDB (MongoDB.Driver 3.x), collections created/indexed + seeded in `Program.cs`
- JWT auth (roles: `Trainer`, `Athlete`), SignalR hubs (`/hubs/chat`, `/hubs/notifications`)
- ML.NET 3 + LightGBM progress prediction (`ML/`, `Services/*ML*`, `PredictionEngineService`)

## Layout
- `Controllers/` thin; business logic lives in `Services/` (registered in `Program.cs`)
- `Models/` = Mongo documents (`[BsonId]`, `[BsonRepresentation(BsonType.ObjectId)]` on ids, enums stored as strings)
- `ML/` feature types, `MuscleOverlap` matrix (focus → muscle group overlap 0..1), synthetic data generator, trainer
- `Settings/` options classes bound from `appsettings.json`

## Conventions
- Follow existing style: async Mongo calls, `IMongoCollection<T>` fetched in service ctor, `[Authorize(Roles = "...")]` on endpoints
- Muscle groups are exactly: Chest, Back, Shoulders, Legs, Arms, Core
- Secrets (JWT key, LLM API key) never in appsettings.json — use `dotnet user-secrets` / env vars
- New features: put pure logic (validators, generators, algorithms) in classes with no Mongo/HTTP dependencies so they are unit-testable

## Commands
- Build: `dotnet build`
- Tests: `dotnet test` (test project: `mobileappbackend1.Tests`)
- Run: `dotnet run --project mobileappbackend1` (Swagger at /swagger in Development)

## Current feature work
AI-assisted training plan generator — full spec and phase list in `docs/ai-plan-generator.md`. Implement ONE phase per session, only what that phase lists.
