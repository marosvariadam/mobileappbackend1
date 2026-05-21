using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.ML;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using mobileappbackend1.Hubs;
using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Services;
using mobileappbackend1.Settings;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);


        builder.Services.Configure<MongoDbSettings>(
            builder.Configuration.GetSection("MongoDbSettings"));

        builder.Services.AddSingleton<IMongoClient>(s =>
        {
            var settings = builder.Configuration.GetSection("MongoDbSettings").Get<MongoDbSettings>();
            return new MongoClient(settings!.ConnectionString);
        });

        builder.Services.AddScoped(s =>
        {
            var client   = s.GetRequiredService<IMongoClient>();
            var settings = builder.Configuration.GetSection("MongoDbSettings").Get<MongoDbSettings>();
            return client.GetDatabase(settings!.DatabaseName);
        });


        builder.Services.AddSingleton<PresenceTracker>();
        builder.Services.AddScoped<UserService>();
        builder.Services.AddScoped<WorkoutService>();
        builder.Services.AddScoped<ExerciseService>();
        builder.Services.AddScoped<MessageService>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<NotificationService>();
        builder.Services.AddScoped<OnboardingFormService>();
        builder.Services.AddScoped<TrainerRequestService>();
        builder.Services.AddScoped<TrainingBlockService>();
        builder.Services.AddScoped<FeatureEngineeringService>();

        // MLContext is thread-safe and reusable - singleton. ProgressTrainer is
        // stateless; scoped keeps it aligned with the other services.
        builder.Services.Configure<MLSettings>(builder.Configuration.GetSection("MLSettings"));
        builder.Services.AddSingleton(_ => new MLContext(seed: 1));
        builder.Services.AddScoped<ProgressTrainer>();
        builder.Services.AddSingleton<PredictionEngineService>();
        builder.Services.AddScoped<SyntheticDataGenerator>();
        builder.Services.AddScoped<MetricsLogService>();
        builder.Services.AddScoped<MLTrainingService>();
        builder.Services.AddHostedService<PeriodicRetrainService>();

        //
        // SignalR WebSocket / SSE transports require the browser to send credentials,
        // so AllowCredentials() is mandatory. ASP.NET Core forbids combining
        // AllowAnyOrigin() with AllowCredentials(), so we use SetIsOriginAllowed
        // as a dev-only fallback when no explicit origins are configured.
        //
        // In production always set Cors:AllowedOrigins in appsettings / env vars.

        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (allowedOrigins.Length > 0)
                    policy.WithOrigins(allowedOrigins)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                else
                    // Dev fallback - not safe for production
                    policy.SetIsOriginAllowed(_ => true)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
            });
        });


        builder.Services.AddSignalR();


        builder.Services.AddHealthChecks();


        builder.Services.AddControllers()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "CoachingApp API", Version = "v1" });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header. Format: Bearer {token}",
                Name        = "Authorization",
                In          = ParameterLocation.Header,
                Type        = SecuritySchemeType.ApiKey,
                Scheme      = "Bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                            { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
                        Scheme = "oauth2",
                        Name   = "Bearer",
                        In     = ParameterLocation.Header
                    },
                    new List<string>()
                }
            });
        });


        var jwtSettings = builder.Configuration.GetSection("JwtSettings");
        var secretKey   = jwtSettings["SecretKey"]
            ?? throw new InvalidOperationException("JwtSettings:SecretKey is not configured.");
        var key = Encoding.ASCII.GetBytes(secretKey);

        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            // Require HTTPS in production; allow HTTP in development only
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.SaveToken = true;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(key),
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidIssuer              = jwtSettings["Issuer"],
                ValidAudience            = jwtSettings["Audience"]
            };

            // SignalR WebSocket upgrades cannot set the Authorization header,
            // so the client passes the JWT as ?access_token= in the query string.
            // We read it here and hand it to the normal JWT validation pipeline.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path        = context.HttpContext.Request.Path;

                    if (!string.IsNullOrEmpty(accessToken) &&
                        (path.StartsWithSegments("/hubs/chat") ||
                         path.StartsWithSegments("/hubs/notifications")))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });


        var app = builder.Build();


        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();

            // Users: unique email
            var users = db.GetCollection<User>("Users");
            users.Indexes.CreateOne(new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.Email),
                new CreateIndexOptions { Unique = true }));

            // Workouts: athlete and trainer lookups
            var workouts = db.GetCollection<Workout>("Workouts");
            workouts.Indexes.CreateOne(new CreateIndexModel<Workout>(
                Builders<Workout>.IndexKeys.Ascending(w => w.AthleteId)));
            workouts.Indexes.CreateOne(new CreateIndexModel<Workout>(
                Builders<Workout>.IndexKeys.Ascending(w => w.TrainerId)));

            // Exercises: trainer filter
            var exercises = db.GetCollection<Exercise>("Exercises");
            exercises.Indexes.CreateOne(new CreateIndexModel<Exercise>(
                Builders<Exercise>.IndexKeys.Ascending(e => e.CreatedByTrainerId)));

            // Messages
            var messages = db.GetCollection<Message>("Messages");

            // Fast conversation fetch + chronological sort
            messages.Indexes.CreateOne(new CreateIndexModel<Message>(
                Builders<Message>.IndexKeys
                    .Ascending(m => m.ConversationId)
                    .Descending(m => m.CreatedAt)));

            // Fast unread-count queries (used in aggregation + MarkAsRead)
            messages.Indexes.CreateOne(new CreateIndexModel<Message>(
                Builders<Message>.IndexKeys
                    .Ascending(m => m.RecipientId)
                    .Ascending(m => m.IsRead)));

            // TrainerRequests
            var trainerRequests = db.GetCollection<TrainerRequest>("TrainerRequests");
            trainerRequests.Indexes.CreateOne(new CreateIndexModel<TrainerRequest>(
                Builders<TrainerRequest>.IndexKeys
                    .Ascending(r => r.TrainerId)
                    .Ascending(r => r.Status)));
            trainerRequests.Indexes.CreateOne(new CreateIndexModel<TrainerRequest>(
                Builders<TrainerRequest>.IndexKeys.Ascending(r => r.AthleteId)));

            // Notifications
            var notifications = db.GetCollection<Notification>("Notifications");
            notifications.Indexes.CreateOne(new CreateIndexModel<Notification>(
                Builders<Notification>.IndexKeys
                    .Ascending(n => n.UserId)
                    .Descending(n => n.CreatedAt)));
            notifications.Indexes.CreateOne(new CreateIndexModel<Notification>(
                Builders<Notification>.IndexKeys
                    .Ascending(n => n.UserId)
                    .Ascending(n => n.IsRead)));

            // OnboardingForms: one form per trainer
            var onboardingForms = db.GetCollection<OnboardingForm>("OnboardingForms");
            onboardingForms.Indexes.CreateOne(new CreateIndexModel<OnboardingForm>(
                Builders<OnboardingForm>.IndexKeys.Ascending(f => f.TrainerId),
                new CreateIndexOptions { Unique = true }));

            // OnboardingResponses: one response per athlete per trainer
            var onboardingResponses = db.GetCollection<OnboardingResponse>("OnboardingResponses");
            onboardingResponses.Indexes.CreateOne(new CreateIndexModel<OnboardingResponse>(
                Builders<OnboardingResponse>.IndexKeys
                    .Ascending(r => r.AthleteId)
                    .Ascending(r => r.TrainerId),
                new CreateIndexOptions { Unique = true }));

            // TrainingBlocks: feature-engineering resolves a block per (athlete, week),
            // so (AthleteId, StartDate) is the hot query path.
            var trainingBlocks = db.GetCollection<TrainingBlock>("TrainingBlocks");
            trainingBlocks.Indexes.CreateOne(new CreateIndexModel<TrainingBlock>(
                Builders<TrainingBlock>.IndexKeys
                    .Ascending(b => b.AthleteId)
                    .Ascending(b => b.StartDate)));

            // ML metrics log: read pattern is "latest first" for the status/drift checks.
            var mlMetrics = db.GetCollection<MetricsLog>("MlMetricsLog");
            mlMetrics.Indexes.CreateOne(new CreateIndexModel<MetricsLog>(
                Builders<MetricsLog>.IndexKeys.Descending(m => m.CreatedAt)));
        }


        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
            var exercises = db.GetCollection<Exercise>("Exercises");

            // Only seed if collection has no system defaults yet
            var hasDefaults = exercises.Find(e => e.CreatedByTrainerId == null).Any();
            if (!hasDefaults)
            {
                var defaults = new List<Exercise>
                {
                    // Chest
                    new() { Name = "Bench Press",            MuscleGroup = "Chest",     Equipment = "Barbell" },
                    new() { Name = "Incline Bench Press",    MuscleGroup = "Chest",     Equipment = "Barbell" },
                    new() { Name = "Dumbbell Fly",           MuscleGroup = "Chest",     Equipment = "Dumbbells" },
                    new() { Name = "Push-Up",                MuscleGroup = "Chest",     Equipment = "Bodyweight" },
                    new() { Name = "Cable Crossover",        MuscleGroup = "Chest",     Equipment = "Cable Machine" },
                    new() { Name = "Chest Dip",              MuscleGroup = "Chest",     Equipment = "Dip Station" },

                    // Back
                    new() { Name = "Deadlift",               MuscleGroup = "Back",      Equipment = "Barbell" },
                    new() { Name = "Barbell Row",             MuscleGroup = "Back",      Equipment = "Barbell" },
                    new() { Name = "Pull-Up",                 MuscleGroup = "Back",      Equipment = "Pull-Up Bar" },
                    new() { Name = "Lat Pulldown",            MuscleGroup = "Back",      Equipment = "Cable Machine" },
                    new() { Name = "Seated Cable Row",        MuscleGroup = "Back",      Equipment = "Cable Machine" },
                    new() { Name = "Dumbbell Row",            MuscleGroup = "Back",      Equipment = "Dumbbells" },
                    new() { Name = "T-Bar Row",               MuscleGroup = "Back",      Equipment = "T-Bar" },

                    // Shoulders
                    new() { Name = "Overhead Press",          MuscleGroup = "Shoulders", Equipment = "Barbell" },
                    new() { Name = "Dumbbell Shoulder Press",  MuscleGroup = "Shoulders", Equipment = "Dumbbells" },
                    new() { Name = "Lateral Raise",           MuscleGroup = "Shoulders", Equipment = "Dumbbells" },
                    new() { Name = "Front Raise",             MuscleGroup = "Shoulders", Equipment = "Dumbbells" },
                    new() { Name = "Face Pull",               MuscleGroup = "Shoulders", Equipment = "Cable Machine" },
                    new() { Name = "Reverse Fly",             MuscleGroup = "Shoulders", Equipment = "Dumbbells" },

                    // Legs
                    new() { Name = "Squat",                   MuscleGroup = "Legs",      Equipment = "Barbell" },
                    new() { Name = "Leg Press",               MuscleGroup = "Legs",      Equipment = "Leg Press Machine" },
                    new() { Name = "Romanian Deadlift",       MuscleGroup = "Legs",      Equipment = "Barbell" },
                    new() { Name = "Leg Curl",                MuscleGroup = "Legs",      Equipment = "Machine" },
                    new() { Name = "Leg Extension",           MuscleGroup = "Legs",      Equipment = "Machine" },
                    new() { Name = "Bulgarian Split Squat",   MuscleGroup = "Legs",      Equipment = "Dumbbells" },
                    new() { Name = "Lunge",                   MuscleGroup = "Legs",      Equipment = "Dumbbells" },
                    new() { Name = "Calf Raise",              MuscleGroup = "Legs",      Equipment = "Machine" },
                    new() { Name = "Hip Thrust",              MuscleGroup = "Legs",      Equipment = "Barbell" },

                    // Arms
                    new() { Name = "Barbell Curl",            MuscleGroup = "Arms",      Equipment = "Barbell" },
                    new() { Name = "Dumbbell Curl",           MuscleGroup = "Arms",      Equipment = "Dumbbells" },
                    new() { Name = "Hammer Curl",             MuscleGroup = "Arms",      Equipment = "Dumbbells" },
                    new() { Name = "Tricep Pushdown",         MuscleGroup = "Arms",      Equipment = "Cable Machine" },
                    new() { Name = "Skull Crusher",           MuscleGroup = "Arms",      Equipment = "Barbell" },
                    new() { Name = "Overhead Tricep Extension", MuscleGroup = "Arms",    Equipment = "Dumbbells" },

                    // Core
                    new() { Name = "Plank",                   MuscleGroup = "Core",      Equipment = "Bodyweight" },
                    new() { Name = "Crunch",                  MuscleGroup = "Core",      Equipment = "Bodyweight" },
                    new() { Name = "Hanging Leg Raise",       MuscleGroup = "Core",      Equipment = "Pull-Up Bar" },
                    new() { Name = "Russian Twist",           MuscleGroup = "Core",      Equipment = "Bodyweight" },
                    new() { Name = "Cable Woodchop",          MuscleGroup = "Core",      Equipment = "Cable Machine" },
                    new() { Name = "Ab Rollout",              MuscleGroup = "Core",      Equipment = "Ab Wheel" },
                };

                exercises.InsertMany(defaults);
            }
        }


        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseCors();           // must be before Auth so OPTIONS pre-flights are handled
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health");
        app.MapControllers();
        app.MapHub<ChatHub>("/hubs/chat");
        app.MapHub<NotificationHub>("/hubs/notifications");

        app.Run();
    }
}
