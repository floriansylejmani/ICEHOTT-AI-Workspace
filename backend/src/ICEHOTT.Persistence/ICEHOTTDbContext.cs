using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Security;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence;

public sealed class ICEHOTTDbContext(DbContextOptions<ICEHOTTDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ICEHOTTDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
