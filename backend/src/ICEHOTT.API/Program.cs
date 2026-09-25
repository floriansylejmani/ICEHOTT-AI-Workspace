using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ICEHOTT.API.Background;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Agents;
using ICEHOTT.Application.Auth;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Application.Tools;
using ICEHOTT.Application.Workspaces;
using ICEHOTT.Infrastructure.Ai;
using ICEHOTT.Infrastructure.Documents;
using ICEHOTT.Infrastructure.Security;
using ICEHOTT.Infrastructure.Tools;
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

var openAiEmbedding = builder.Configuration
    .GetSection(OpenAiEmbeddingOptions.SectionName)
    .Get<OpenAiEmbeddingOptions>() ?? new OpenAiEmbeddingOptions();
if (!Uri.TryCreate(openAiEmbedding.BaseUrl, UriKind.Absolute, out var openAiEmbeddingUri))
    throw new InvalidOperationException("OpenAiEmbedding:BaseUrl must be an absolute URI.");

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (!builder.Environment.IsDevelopment() && allowedOrigins.Length == 0)
    throw new InvalidOperationException("Cors:AllowedOrigins must be configured outside Development.");

var autoMigrate = builder.Configuration.GetValue<bool>("Database:AutoMigrate");
var authRateLimitPermit = Math.Clamp(
    builder.Configuration.GetValue<int?>("RateLimiting:AuthPermitLimit") ?? 10,
    1,
    1000);
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
builder.Services.Configure<OpenAiEmbeddingOptions>(
    builder.Configuration.GetSection(OpenAiEmbeddingOptions.SectionName));
builder.Services.Configure<KnowledgeWorkerOptions>(
    builder.Configuration.GetSection(KnowledgeWorkerOptions.SectionName));

builder.Services.AddDbContext<ICEHOTTDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRefreshSessionRepository, RefreshSessionRepository>();
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IKnowledgeRepository, KnowledgeRepository>();
builder.Services.AddScoped<IKnowledgeJobQueue, KnowledgeJobQueue>();
builder.Services.AddScoped<IToolExecutionRepository, ToolExecutionRepository>();
builder.Services.AddScoped<IToolPolicyRepository, ToolPolicyRepository>();
builder.Services.AddScoped<IWorkspaceAuditNoteRepository, WorkspaceAuditNoteRepository>();
builder.Services.AddScoped<IVectorStore, PostgresVectorStore>();
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ICEHOTTDbContext>());

// B1: Profile repository and resolvers
builder.Services.AddScoped<IEmbeddingProfileRepository, EmbeddingProfileRepository>();
builder.Services.AddScoped<IServingEmbeddingProfileResolver, ServingEmbeddingProfileResolver>();
builder.Services.AddScoped<IBuildEmbeddingProfileResolver, BuildEmbeddingProfileResolver>();
builder.Services.AddScoped<IVectorIndexProvisioner, PostgresVectorIndexProvisioner>();
builder.Services.AddScoped<IEmbeddingBuildStore, PostgresEmbeddingBuildStore>();
builder.Services.AddScoped<IRagEvaluationEvidenceRepository, RagEvaluationEvidenceRepository>();
builder.Services.AddScoped<IEmbeddingProfileActivationStore, PostgresEmbeddingProfileActivationStore>();

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

builder.Services.AddHttpClient<OpenAiEmbeddingProvider>(client =>
{
    client.BaseAddress = new Uri(openAiEmbeddingUri.ToString().TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(
        Math.Clamp(openAiEmbedding.TimeoutSeconds, 5, 120));
});

// B2/B5: provider registry discovers all explicitly registered provider adapters.
builder.Services.AddScoped<IEmbeddingProvider, AiRuntimeEmbeddingProvider>();
builder.Services.AddScoped<IEmbeddingProvider>(
    sp => sp.GetRequiredService<OpenAiEmbeddingProvider>());
builder.Services.AddScoped<IEmbeddingProviderRegistry, EmbeddingProviderRegistry>();

builder.Services.AddScoped<IProfileKnowledgeRetriever, ProfileKnowledgeRetriever>();
builder.Services.AddScoped<IKnowledgeRetriever, KnowledgeRetriever>();

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<KnowledgeIndexingProcessor>();
builder.Services.AddScoped<EmbeddingProfileManagementService>();
builder.Services.AddScoped<EmbeddingProfileBuildService>();
builder.Services.AddScoped<EmbeddingProfileActivationService>();
builder.Services.AddScoped<RagBenchmarkRunner>();
builder.Services.AddScoped<IWorkspaceTool, WorkspaceEchoTool>();
builder.Services.AddScoped<IWorkspaceTool, WorkspaceAuditNoteCreateTool>();
builder.Services.AddScoped<IToolRegistry, ToolRegistry>();
builder.Services.AddSingleton(builder.Configuration.GetSection(ToolQuotaOptions.SectionName).Get<ToolQuotaOptions>() ?? new ToolQuotaOptions());
builder.Services.AddSingleton<IToolOperationalLog, ToolOperationalLog>();
builder.Services.AddScoped<ToolExecutionService>();
builder.Services.AddScoped<ToolPolicyService>();

var promotionRequirements = RagPromotionRequirements.ProviderBenchmarkDefault with
{
    RequireOfflineSemanticEvidence = builder.Configuration.GetValue<bool>(
        "RagPromotion:RequireOfflineSemanticEvidence")
};
builder.Services.AddSingleton(promotionRequirements);
builder.Services.AddSingleton<RagPromotionPolicy>();

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
                PermitLimit = authRateLimitPermit,
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
    IServingEmbeddingProfileResolver servingProfileResolver,
    IEmbeddingProviderRegistry providerRegistry,
    IVectorStore vectorStore,
    CancellationToken cancellationToken) =>
{
    var databaseReady = await db.Database.CanConnectAsync(cancellationToken);
    var aiReady = await aiRuntimeClient.IsReadyAsync(cancellationToken);

    EmbeddingProfileDescriptor? activeProfile = null;
    var embeddingProfileReady = false;
    var embeddingProviderReady = false;

    if (databaseReady)
    {
        try
        {
            activeProfile = await servingProfileResolver.ResolveAsync(cancellationToken);
            embeddingProfileReady = await vectorStore.IsProfileReadyAsync(
                activeProfile, cancellationToken);

            var provider = providerRegistry.Resolve(activeProfile.Provider);
            embeddingProviderReady =
                provider is IEmbeddingProviderConfigurationProbe probe &&
                probe.IsConfigured(activeProfile);
        }
        catch (InvalidOperationException)
        {
            // No Active profile exists; readiness flags stay false.
        }
        catch (EmbeddingProviderException)
        {
            // Provider is unavailable or not registered.
        }
    }

    var profileKey = activeProfile?.Key ?? "none";

    return databaseReady &&
           aiReady &&
           embeddingProfileReady &&
           embeddingProviderReady
        ? Results.Ok(new
        {
            status = "ready",
            service = "icehott-api",
            database = "ready",
            aiRuntime = "ready",
            embeddingProfile = profileKey,
            embeddingProfileReady = true,
            embeddingProviderReady = true
        })
        : Results.Json(
            new
            {
                status = "not_ready",
                service = "icehott-api",
                database = databaseReady ? "ready" : "unavailable",
                aiRuntime = aiReady ? "ready" : "unavailable",
                embeddingProfile = profileKey,
                embeddingProfileReady,
                embeddingProviderReady
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

public partial class Program;
