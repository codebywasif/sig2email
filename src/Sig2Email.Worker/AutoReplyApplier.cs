using Microsoft.EntityFrameworkCore;
using Sig2Email.Core.Models;
using Sig2Email.Data;

namespace Sig2Email.Worker;

/// <summary>
/// Reconciles central auto-reply (out-of-office) policies with mailboxes through Microsoft Graph
/// <c>mailboxSettings</c>. Exchange itself starts and stops the reply at the scheduled times; this service pushes
/// the schedule, re-asserts it periodically (so users cannot quietly switch it off), manages the optional
/// forwarding inbox rule for the leave period and reverts everything when a policy is disabled or deleted.
/// </summary>
public sealed class AutoReplyApplier(AppDbContext db, IGraphClient graph, ILogger<AutoReplyApplier> log)
{
    private static readonly TimeSpan Reassert = TimeSpan.FromHours(6);
    private const string ForwardRuleName = "Sig2Email auto-reply forwarding";

    public async Task RunAsync(Guid tenantId, string entraTenantId, CancellationToken ct)
    {
        db.SystemScope = true;
        var now = DateTime.UtcNow;
        var horizon = now.AddDays(-1);
        var policies = await db.AutoReplyPolicies.Where(p => p.TenantId == tenantId && (p.Dirty || (p.Enabled && !p.Deleted && p.EndUtc > horizon))).ToListAsync(ct);
        foreach (var policy in policies)
        {
            try
            {
                await ReconcileAsync(policy, entraTenantId, now, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError("Auto-reply policy {Policy} failed: {Message}", policy.Id, e.Message);
            }
        }
    }

    private async Task ReconcileAsync(AutoReplyPolicy policy, string tid, DateTime now, CancellationToken ct)
    {
        var states = await db.AutoReplyStates.Where(s => s.PolicyId == policy.Id).ToListAsync(ct);
        var active = policy.Enabled && !policy.Deleted;

        var targets = new HashSet<string>();
        if (active)
        {
            foreach (var id in policy.UserIds) targets.Add(id);
            if (policy.GroupIds.Count > 0)
            {
                // Group membership is stored as JSON, so it is matched in memory.
                var members = await db.Users.AsNoTracking().Where(u => u.TenantId == policy.TenantId).Select(u => new { u.Id, u.GroupIds }).ToListAsync(ct);
                foreach (var m in members.Where(m => m.GroupIds.Any(policy.GroupIds.Contains))) targets.Add(m.Id);
            }
            var existing = await db.Users.Where(u => u.TenantId == policy.TenantId && targets.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
            targets.IntersectWith(existing);
        }

        // Mailboxes that are no longer covered (policy disabled/deleted, or user removed from it).
        foreach (var state in states.Where(s => !targets.Contains(s.UserId) && s.RevertedAt == null))
            await Try(state, async () =>
            {
                if (now < policy.EndUtc || !active)
                    await graph.SendAsync(tid, HttpMethod.Patch, $"/users/{state.UserId}/mailboxSettings",
                        new { automaticRepliesSetting = new { status = "disabled" } }, ct);
                await RemoveForwardAsync(state, tid, ct);
                state.RevertedAt = now;
            });

        var withinLeave = now >= policy.StartUtc && now < policy.EndUtc;
        foreach (var userId in targets)
        {
            var state = states.FirstOrDefault(s => s.UserId == userId);
            if (state == null) db.AutoReplyStates.Add(state = new AutoReplyState { TenantId = policy.TenantId, PolicyId = policy.Id, UserId = userId });
            await Try(state, async () =>
            {
                var due = policy.Dirty || state.AppliedAt == null || state.RevertedAt != null || state.Error != null || state.AppliedAt < now - Reassert;
                if (due && now < policy.EndUtc)
                {
                    await graph.SendAsync(tid, HttpMethod.Patch, $"/users/{userId}/mailboxSettings", new
                    {
                        automaticRepliesSetting = new
                        {
                            status = "scheduled",
                            externalAudience = policy.ExternalAudience switch
                            {
                                ExternalAudience.None => "none", ExternalAudience.ContactsOnly => "contactsOnly", _ => "all",
                            },
                            scheduledStartDateTime = new { dateTime = policy.StartUtc.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                            scheduledEndDateTime = new { dateTime = policy.EndUtc.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = "UTC" },
                            internalReplyMessage = policy.InternalMessage,
                            externalReplyMessage = policy.ExternalMessage,
                        },
                    }, ct);
                    state.AppliedAt = now;
                    state.RevertedAt = null;
                }

                // Forwarding only exists while the person is actually away.
                var wantForward = withinLeave && !string.IsNullOrEmpty(policy.ForwardTo);
                if (state.ForwardRuleId != null && (!wantForward || policy.Dirty)) await RemoveForwardAsync(state, tid, ct);
                if (wantForward && state.ForwardRuleId == null)
                {
                    using var created = await graph.SendAsync(tid, HttpMethod.Post, $"/users/{userId}/mailFolders/inbox/messageRules", new
                    {
                        displayName = ForwardRuleName,
                        sequence = 1,
                        isEnabled = true,
                        actions = new { forwardTo = new[] { new { emailAddress = new { address = policy.ForwardTo } } }, stopProcessingRules = false },
                    }, ct);
                    state.ForwardRuleId = created?.RootElement.GetProperty("id").GetString();
                }
            });
        }

        policy.Dirty = false;
        if (policy.Deleted && states.All(s => s.RevertedAt != null || s.AppliedAt == null))
        {
            db.AutoReplyStates.RemoveRange(states);
            db.AutoReplyPolicies.Remove(policy);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RemoveForwardAsync(AutoReplyState state, string tid, CancellationToken ct)
    {
        if (state.ForwardRuleId == null) return;
        try
        {
            await graph.SendAsync(tid, HttpMethod.Delete, $"/users/{state.UserId}/mailFolders/inbox/messageRules/{state.ForwardRuleId}", null, ct);
        }
        catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.NotFound) { /* already gone */ }
        state.ForwardRuleId = null;
    }

    private async Task Try(AutoReplyState state, Func<Task> action)
    {
        try
        {
            await action();
            state.Error = null;
        }
        catch (GraphException e)
        {
            state.Error = e.Message.Length > 400 ? e.Message[..400] : e.Message;
            log.LogWarning("Auto-reply for mailbox {User} failed: {Message}", state.UserId, e.Message);
        }
    }
}
