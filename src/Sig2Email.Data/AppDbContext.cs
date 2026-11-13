using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sig2Email.Core.Models;

namespace Sig2Email.Data;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>
/// Multi-tenant store. Every tenant-owned entity carries a global query filter, so a context that has neither
/// <see cref="TenantId"/> nor <see cref="SystemScope"/> set sees no tenant data at all (deny by default).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Tenant the current request is bound to.</summary>
    public Guid? TenantId { get; set; }
    /// <summary>Cross-tenant access for background workers and the mail pipeline.</summary>
    public bool SystemScope { get; set; }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<SystemSettingsRow> SystemSettings => Set<SystemSettingsRow>();
    public DbSet<DirectoryUser> Users => Set<DirectoryUser>();
    public DbSet<DirectoryAddress> Addresses => Set<DirectoryAddress>();
    public DbSet<DirectoryGroup> Groups => Set<DirectoryGroup>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AutoReplyPolicy> AutoReplyPolicies => Set<AutoReplyPolicy>();
    public DbSet<AutoReplyState> AutoReplyStates => Set<AutoReplyState>();
    public DbSet<Survey> Surveys => Set<Survey>();
    public DbSet<SurveyDispatch> SurveyDispatches => Set<SurveyDispatch>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<LinkClick> LinkClicks => Set<LinkClick>();
    public DbSet<MailStatDaily> MailStats => Set<MailStatDaily>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.EntraTenantId);
            e.Property(x => x.Domains).HasJson();
        });
        b.Entity<TenantDomain>(e => e.HasKey(x => x.Domain));
        b.Entity<Account>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.TenantId);
        });
        b.Entity<SystemSettingsRow>(e => e.Property(x => x.Id).ValueGeneratedNever());
        b.Entity<DirectoryUser>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.DisplayName });
            e.Property(x => x.Addresses).HasJson();
            e.Property(x => x.CustomAttributes).HasJson();
            e.Property(x => x.GroupIds).HasJson();
        });
        b.Entity<DirectoryAddress>(e => e.HasKey(x => new { x.TenantId, x.Address }));
        b.Entity<DirectoryGroup>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.MemberIds).HasJson();
        });
        b.Entity<Template>(e => e.HasIndex(x => x.TenantId));
        b.Entity<TemplateVersion>(e => e.HasIndex(x => new { x.TenantId, x.TemplateId, x.Version }).IsUnique());
        b.Entity<Rule>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Priority });
            e.Property(x => x.Conditions).HasJson();
        });
        b.Entity<Asset>(e => e.HasIndex(x => x.TenantId));
        b.Entity<AutoReplyPolicy>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.UserIds).HasJson();
            e.Property(x => x.GroupIds).HasJson();
        });
        b.Entity<AutoReplyState>(e => e.HasIndex(x => new { x.TenantId, x.PolicyId, x.UserId }).IsUnique());
        b.Entity<Survey>(e =>
        {
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.CustomIcons).HasJson();
            e.Property(x => x.AlertEmails).HasJson();
        });
        b.Entity<SurveyDispatch>(e => e.HasIndex(x => new { x.TenantId, x.SurveyId, x.CreatedAt }));
        b.Entity<SurveyResponse>(e =>
        {
            e.HasIndex(x => x.DispatchId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.SurveyId, x.CreatedAt });
        });
        b.Entity<LinkClick>(e => e.HasIndex(x => new { x.TenantId, x.CreatedAt }));
        b.Entity<MailStatDaily>(e => e.HasKey(x => new { x.TenantId, x.Day }));
        b.Entity<AuditLog>(e => e.HasIndex(x => new { x.TenantId, x.At }));

        var utc = new ValueConverter<DateTime, DateTime>(v => DateTime.SpecifyKind(v, DateTimeKind.Utc), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
            {
                if (p.ClrType == typeof(DateTime)) p.SetValueConverter(utc);
                else if (p.ClrType == typeof(DateTime?)) p.SetValueConverter(utcNullable);
            }
            if (typeof(ITenantOwned).IsAssignableFrom(entity.ClrType))
                typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(entity.ClrType).Invoke(this, new object[] { b });
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned =>
        b.Entity<T>().HasQueryFilter(e => SystemScope || e.TenantId == TenantId);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        if (!SystemScope)
        {
            foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
                if (TenantId == null) throw new InvalidOperationException("No tenant bound to this context.");
                if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty) entry.Entity.TenantId = TenantId.Value;
                if (entry.Entity.TenantId != TenantId.Value)
                    throw new InvalidOperationException("Cross-tenant write blocked.");
            }
        }
        return base.SaveChangesAsync(ct);
    }
}

internal static class ModelExtensions
{
    public static PropertyBuilder<T> HasJson<T>(this PropertyBuilder<T> p) where T : class, new()
    {
        p.HasConversion(
            v => JsonSerializer.Serialize(v, JsonDefaults.Options),
            v => JsonSerializer.Deserialize<T>(v, JsonDefaults.Options) ?? new T(),
            new ValueComparer<T>(
                (a, c) => JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(c, JsonDefaults.Options),
                v => JsonSerializer.Serialize(v, JsonDefaults.Options).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonDefaults.Options), JsonDefaults.Options)!));
        return p;
    }
}
