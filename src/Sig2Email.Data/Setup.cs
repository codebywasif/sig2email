using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sig2Email.Core.Security;
using StackExchange.Redis;

namespace Sig2Email.Data;

public static class Setup
{
    public static IServiceCollection AddSig2EmailData(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var section = config.GetSection(Sig2EmailOptions.Section);
        services.Configure<Sig2EmailOptions>(section);
        var opt = section.Get<Sig2EmailOptions>() ?? new Sig2EmailOptions();

        services.AddDbContext<AppDbContext>(o => Configure(o, opt));
        services.AddMemoryCache();

        if (!string.IsNullOrWhiteSpace(opt.Redis))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(opt.Redis));
            services.AddSingleton<ICacheStore, RedisCacheStore>();
        }
        else services.AddSingleton<ICacheStore, MemoryCacheStore>();

        if (opt.BlobProvider.Equals("azure", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IBlobStore>(_ => new AzureBlobStore(
                opt.BlobConnectionString ?? throw new InvalidOperationException("Sig2Email:BlobConnectionString is required."), opt.BlobContainer));
        else
            services.AddSingleton<IBlobStore>(_ => new FileBlobStore(Path.Combine(opt.ResolveDataDir(), "blobs")));

        var keys = new Lazy<AppKeys>(() => new AppKeys(opt.ResolveSigningKey()));
        services.AddSingleton(_ => keys.Value);
        services.AddSingleton(_ => new TokenSigner(keys.Value.Signing));
        services.AddSingleton(new RuntimeEnvironment(env.IsDevelopment()));
        services.AddSingleton<SettingsProvider>();
        services.AddHostedService<SettingsRefresher>();
        services.AddSingleton<RuntimeProvider>();
        services.AddSingleton<DispatchQueue>();
        services.AddSingleton<MailStatsCollector>();
        services.AddSingleton<SignatureService>();
        services.AddHostedService<BackgroundWriter>();
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder o, Sig2EmailOptions opt)
    {
        if (opt.DatabaseProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
            o.UseNpgsql(opt.ConnectionString ?? throw new InvalidOperationException("Sig2Email:ConnectionString is required for postgres."));
        else
            o.UseSqlite(opt.ConnectionString ?? $"Data Source={Path.Combine(opt.ResolveDataDir(), "sig2email-v2.db")}");
    }

    /// <summary>
    /// Prepares the database. The schema owner (the API) creates/migrates and optionally seeds; other
    /// processes wait until the schema is there.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, bool schemaOwner, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var opt = scope.ServiceProvider.GetRequiredService<IOptions<Sig2EmailOptions>>().Value;
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Sig2Email.Database");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        var postgres = opt.DatabaseProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase);

        if (schemaOwner)
        {
            if (postgres)
            {
                if (opt.AutoMigrate) await db.Database.MigrateAsync(ct);
            }
            else
            {
                await db.Database.EnsureCreatedAsync(ct);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            }
            if (opt.SeedDemoData)
            {
                if (!scope.ServiceProvider.GetRequiredService<RuntimeEnvironment>().IsDevelopment)
                    throw new InvalidOperationException("Sig2Email:SeedDemoData creates accounts with a published password and is only allowed in the Development environment.");
                await DemoSeeder.SeedAsync(scope.ServiceProvider, ct);
            }
            await scope.ServiceProvider.GetRequiredService<SettingsProvider>().RefreshAsync(ct);
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.Tenants.AnyAsync(ct);
                await scope.ServiceProvider.GetRequiredService<SettingsProvider>().RefreshAsync(ct);
                return;
            }
            catch (Exception e) when (attempt < 30)
            {
                log.LogInformation("Waiting for the database schema ({Message})", e.Message);
                await Task.Delay(2000, ct);
            }
        }
    }
}

/// <summary>Used by `dotnet ef migrations add` – migrations target PostgreSQL.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=sig2email;Username=postgres").Options);
}
