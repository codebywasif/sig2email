using Microsoft.EntityFrameworkCore;
using Sig2Email.Data;
using Sig2Email.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSig2EmailData(builder.Configuration, builder.Environment);
builder.Services.AddHttpClient<IGraphClient, GraphClient>(c => c.Timeout = TimeSpan.FromSeconds(100));
builder.Services.AddScoped<DirectorySyncer>();
builder.Services.AddScoped<AutoReplyApplier>();
builder.Services.AddHostedService<DirectorySyncService>();
builder.Services.AddHostedService<AutoReplyService>();

var host = builder.Build();
await host.Services.InitializeDatabaseAsync(schemaOwner: false);
host.Run();

/// <summary>Organisations whose Microsoft 365 connection has passed its test (Settings → System).</summary>
internal static class TenantQuery
{
    public static async Task<List<(Guid Id, string EntraId, DateTime? LastSyncAt, bool SyncRequested)>> ListAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        var rows = await db.Tenants.AsNoTracking().Where(t => t.ConsentGranted && t.EntraTenantId != "")
            .Select(t => new { t.Id, t.EntraTenantId, t.LastSyncAt, t.SyncRequested }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.EntraTenantId, r.LastSyncAt, r.SyncRequested)).ToList();
    }
}

public sealed class DirectorySyncService(IServiceScopeFactory scopes, IGraphClient graph, SettingsProvider settings, ILogger<DirectorySyncService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var idle = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // The Entra app registration is entered in the portal, possibly long after this process started.
                if (!graph.IsConfigured)
                {
                    if (!idle) log.LogInformation("Microsoft Entra is not configured yet (Settings → System) – directory sync is idle");
                    idle = true;
                    await Task.Delay(TimeSpan.FromSeconds(10), stop);
                    continue;
                }
                idle = false;
                var interval = TimeSpan.FromMinutes(Math.Max(5, settings.Current.SyncIntervalMinutes));
                List<(Guid Id, string EntraId, DateTime? LastSyncAt, bool SyncRequested)> tenants;
                using (var scope = scopes.CreateScope()) tenants = await TenantQuery.ListAsync(scope.ServiceProvider, stop);

                foreach (var t in tenants.Where(t => t.SyncRequested || t.LastSyncAt == null || t.LastSyncAt < DateTime.UtcNow - interval))
                {
                    using var scope = scopes.CreateScope();
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<DirectorySyncer>().SyncAsync(t.Id, stop);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogError("Directory sync failed for tenant {Tenant}: {Message}", t.Id, e.Message);
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        db.ChangeTracker.Clear();
                        var message = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                        // LastSyncAt moves on so a broken tenant is retried at the normal interval, not in a tight loop.
                        await db.Tenants.Where(x => x.Id == t.Id).ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.LastSyncError, message)
                            .SetProperty(x => x.LastSyncAt, DateTime.UtcNow)
                            .SetProperty(x => x.SyncRequested, false), stop);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Directory sync loop error");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stop).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}

public sealed class AutoReplyService(IServiceScopeFactory scopes, IGraphClient graph, SettingsProvider settings, ILogger<AutoReplyService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!graph.IsConfigured)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stop);
                    continue;
                }
                List<(Guid Id, string EntraId, DateTime? LastSyncAt, bool SyncRequested)> tenants;
                using (var scope = scopes.CreateScope()) tenants = await TenantQuery.ListAsync(scope.ServiceProvider, stop);
                foreach (var t in tenants)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AutoReplyApplier>().RunAsync(t.Id, t.EntraId, stop);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Auto-reply loop error");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, settings.Current.AutoReplyIntervalSeconds)), stop).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
