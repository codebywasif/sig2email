using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sig2Email.Core.Models;

namespace Sig2Email.Data;

/// <summary>Hands survey-dispatch metadata to a background writer so the mail path never waits on the database.</summary>
public sealed class DispatchQueue
{
    private readonly Channel<SurveyDispatch> _channel = Channel.CreateBounded<SurveyDispatch>(
        new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(SurveyDispatch d) => _channel.Writer.TryWrite(d);
    internal ChannelReader<SurveyDispatch> Reader => _channel.Reader;
}

public sealed class MailStatsCollector
{
    private readonly ConcurrentDictionary<Guid, long[]> _counters = new();

    public void Record(Guid tenantId, bool stamped, bool failed)
    {
        var c = _counters.GetOrAdd(tenantId, _ => new long[3]);
        Interlocked.Increment(ref c[0]);
        if (stamped) Interlocked.Increment(ref c[1]);
        if (failed) Interlocked.Increment(ref c[2]);
    }

    internal List<(Guid tenant, long processed, long stamped, long failed)> Drain()
    {
        var list = new List<(Guid, long, long, long)>();
        foreach (var (tenant, c) in _counters)
        {
            var p = Interlocked.Exchange(ref c[0], 0);
            var s = Interlocked.Exchange(ref c[1], 0);
            var f = Interlocked.Exchange(ref c[2], 0);
            if (p + s + f > 0) list.Add((tenant, p, s, f));
        }
        return list;
    }
}

public sealed class BackgroundWriter(IServiceScopeFactory scopes, DispatchQueue queue, MailStatsCollector stats, ILogger<BackgroundWriter> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var lastStats = DateTime.UtcNow;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var batch = new List<SurveyDispatch>();
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        if (await queue.Reader.WaitToReadAsync(cts.Token))
                            while (batch.Count < 500 && queue.Reader.TryRead(out var d)) batch.Add(d);
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                }
                if (batch.Count > 0) await WriteDispatchesAsync(batch, stop);
                if (DateTime.UtcNow - lastStats > TimeSpan.FromSeconds(10))
                {
                    lastStats = DateTime.UtcNow;
                    await FlushStatsAsync(stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogError(e, "Background writer failed");
                await Task.Delay(1000, stop).ContinueWith(_ => { });
            }
        }
        try { await FlushStatsAsync(CancellationToken.None); } catch { /* shutting down */ }
    }

    private async Task WriteDispatchesAsync(List<SurveyDispatch> batch, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        // Later entries win: a compose session re-renders as recipients change.
        var latest = batch.GroupBy(d => d.Id).Select(g => g.Last()).ToList();
        var ids = latest.Select(d => d.Id).ToList();
        var existing = await db.SurveyDispatches.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        foreach (var d in latest)
        {
            if (existing.TryGetValue(d.Id, out var e))
            {
                e.Recipients = d.Recipients;
                e.Subject = d.Subject;
            }
            else db.SurveyDispatches.Add(d);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task FlushStatsAsync(CancellationToken ct)
    {
        var drained = stats.Drain();
        if (drained.Count == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SystemScope = true;
        var day = DateTime.UtcNow.Date;
        foreach (var (tenant, p, s, f) in drained)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var updated = await db.MailStats.Where(m => m.TenantId == tenant && m.Day == day).ExecuteUpdateAsync(u => u
                    .SetProperty(m => m.Processed, m => m.Processed + p)
                    .SetProperty(m => m.Stamped, m => m.Stamped + s)
                    .SetProperty(m => m.Failed, m => m.Failed + f), ct);
                if (updated > 0) break;
                try
                {
                    db.MailStats.Add(new MailStatDaily { TenantId = tenant, Day = day, Processed = p, Stamped = s, Failed = f });
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear(); // another node inserted the row first – retry as an update
                }
            }
        }
    }
}
