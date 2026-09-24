using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ICEHOTT.API.Background;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Agents;
using ICEHOTT.Application.Auth;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Application.Workspaces;
using ICEHOTT.Infrastructure.Ai;
using ICEHOTT.Infrastructure.Documents;
using ICEHOTT.Infrastructure.Security;
using ICEHOTT.Persistence;
using ICEHOTT.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be configured with at least 32 characters.");

var aiRuntime = builder.Configuration.GetSection(AiRuntimeOptions.SectionName).Get<AiRuntimeOptions>() ?? new AiRuntimeOptions();
if (!Uri.TryCreate(aiRuntime.BaseUrl, UriKind.Absolute, out var aiRuntimeUri))
    throw new InvalidOperationException("AiRuntime:BaseUrl must be an absolute URI.");

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (!builder.Environment.IsDevelopment() && allowedOrigins.Length == 0)
    throw new InvalidOperationException("Cors:AllowedOrigins must be configured outside Development.");

var autoMigrate = builder.Configuration.GetValue<bool>("Database:AutoMigrate");
var knowledgeWorker = builder.Configuration
    .GetSection(KnowledgeWorkerOptions.SectionName)
    .Get<KnowledgeWorkerOptions>() ?? new KnowledgeWorkerOptions();

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<AiRuntimeOptions>(builder.Configuration.GetSection(AiRuntimeOptions.SectionName));
builder.Services.Configure<KnowledgeWorkerOptions>(
    builder.Configuration.GetSection(KnowledgeWorkerOptions.SectionName));

builder.Services.AddDbContext<ICEHOTTDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRefreshSessionRepository, RefreshSessionRepository>();
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IKnowledgeRepository, KnowledgeRepository>();
builder.Services.AddScoped<IKnowledgeJobQueue, KnowledgeJobQueue>();
builder.Services.AddScoped<IVectorIndexProvisioner, PostgresVectorIndexProvisioner>();
builder.Services.AddScoped<IEmbeddingProfileCoverageService, PostgresEmbeddingProfileCoverageService>();
builder.Services.AddScoped<IVectorStore, PostgresVectorStore>();
builder.Services.AddScoped<IEmbeddingProfileRepository, EmbeddingProfileRepository>();
builder.Services.AddScoped<IServingEmbeddingProfileResolver, ServingEmbeddingProfileResolver>();
builder.Services.AddScoped<IBuildEmbeddingProfileResolver, BuildEmbeddingProfileResolver>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ICEHOTTDbContext>());

builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddSingleton<IKnowledgeChunker, StructureAwareKnowledgeChunker>();
builder.Services.AddSingleton<IRagReranker, HybridRagReranker>();
builder.Services.AddSingleton<IRetrievedContentPolicy, RetrievedContentPolicy>();

builder.Services.AddHttpClient<IAiRuntimeClient, AiRuntimeClient>(client =>
{
    client.BaseAddress = new Uri(aiRuntimeUri.ToString().TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(aiRuntime.TimeoutSeconds, 5, 120));
});
builder.Services.AddScoped<IEmbeddingProvider, AiRuntimeEmbeddingProvider>();
builder.Services.AddScoped<IEmbeddingProviderRegistry, EmbeddingProviderRegistry>();
builder.Services.AddScoped<IKnowledgeRetriever, KnowledgeRetriever>();

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<KnowledgeIndexingProcessor>();

if (knowledgeWorker.Enabled)
    builder.Services.AddHostedService<KnowledgeIngestionWorker>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ClockSkew = TimeSpan.Zero
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    else
        policy.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

var app = builder.Build();

if (autoMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
    await db.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/ready", async (
    ICEHOTTDbContext db,
    IAiRuntimeClient aiRuntimeClient,
    IServingEmbeddingProfileResolver servingProfiles,
    IEmbeddingProviderRegistry embeddingProviders,
    IVectorStore vectorStore,
    CancellationToken cancellationToken) =>
{
    var databaseReady = await db.Database.CanConnectAsync(cancellationToken);
    var aiReady = await aiRuntimeClient.IsReadyAsync(cancellationToken);

    EmbeddingProfileDescriptor? profile = null;
    var embeddingProfileReady = false;

    if (databaseReady)
    {
        try
        {
            profile = await servingProfiles.ResolveAsync(cancellationToken);
            var provider = embeddingProviders.Resolve(profile.Provider);
            provider.Capabilities.ValidateProfile(profile);
            embeddingProfileReady = await vectorStore.IsProfileReadyAsync(
                profile,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or EmbeddingProviderException)
        {
            embeddingProfileReady = false;
        }
    }

    return databaseReady && aiReady && embeddingProfileReady
        ? Results.Ok(new
        {
            status = "ready",
            service = "icehott-api",
            database = "ready",
            aiRuntime = "ready",
            embeddingProfile = profile?.Key,
            embeddingProfileReady = true
        })
        : Results.Json(
            new
            {
                status = "not_ready",
                service = "icehott-api",
                database = databaseReady ? "ready" : "unavailable",
                aiRuntime = aiReady ? "ready" : "unavailable",
                embeddingProfile = profile?.Key,
                embeddingProfileReady
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program;
