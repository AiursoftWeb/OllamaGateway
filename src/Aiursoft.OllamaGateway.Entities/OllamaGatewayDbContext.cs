using System.Diagnostics.CodeAnalysis;
using Aiursoft.DbTools;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Entities;

[ExcludeFromCodeCoverage]

public abstract class TemplateDbContext(DbContextOptions options) : IdentityDbContext<User>(options), ICanMigrate
{
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<VirtualModel> VirtualModels => Set<VirtualModel>();
    public DbSet<VirtualModelBackend> VirtualModelBackends => Set<VirtualModelBackend>();
    public DbSet<OllamaProvider> OllamaProviders => Set<OllamaProvider>();
    public DbSet<UnderlyingModelUsage> UnderlyingModelUsages => Set<UnderlyingModelUsage>();

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<UnderlyingModelUsage>()
            .HasIndex(u => new { u.ProviderId, u.ModelName })
            .IsUnique();
    }
}
