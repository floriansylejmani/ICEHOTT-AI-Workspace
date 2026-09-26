using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Agents;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Domain.Security;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence;

public sealed class ICEHOTTDbContext(DbContextOptions<ICEHOTTDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ConversationMessageCitation> ConversationMessageCitations => Set<ConversationMessageCitation>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
    public DbSet<EmbeddingProfile> EmbeddingProfiles => Set<EmbeddingProfile>();
    public DbSet<RagEvaluationEvidence> RagEvaluationEvidence => Set<RagEvaluationEvidence>();
    public DbSet<KnowledgeProcessingJob> KnowledgeProcessingJobs => Set<KnowledgeProcessingJob>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();
    public DbSet<ToolExecutionAuditEvent> ToolExecutionAuditEvents => Set<ToolExecutionAuditEvent>();
    public DbSet<WorkspaceAuditNote> WorkspaceAuditNotes => Set<WorkspaceAuditNote>();
    public DbSet<ToolPolicy> ToolPolicies => Set<ToolPolicy>();
    public DbSet<ToolPolicyAuditEvent> ToolPolicyAuditEvents => Set<ToolPolicyAuditEvent>();
    public DbSet<ToolQuotaCounter> ToolQuotaCounters => Set<ToolQuotaCounter>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<WorkflowStepRun> WorkflowStepRuns => Set<WorkflowStepRun>();
    public DbSet<WorkflowCheckpoint> WorkflowCheckpoints => Set<WorkflowCheckpoint>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<WorkflowTrigger> WorkflowTriggers => Set<WorkflowTrigger>();
    public DbSet<WorkflowTriggerFire> WorkflowTriggerFires => Set<WorkflowTriggerFire>();
    public DbSet<WorkflowAuditEvent> WorkflowAuditEvents => Set<WorkflowAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ICEHOTTDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
