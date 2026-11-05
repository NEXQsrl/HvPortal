using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

public static class JobEndpoints
{
    public static object Dto(Job j) => new
    {
        j.Id,
        j.Type,
        j.Operation,
        targetType = j.TargetType.ToString(),
        j.TargetId,
        j.TargetName,
        j.HostId,
        j.RequestedByUserId,
        status = j.Status.ToString(),
        j.Progress,
        j.ProgressMessage,
        j.CreatedAt,
        j.StartedAt,
        j.DispatchedAt,
        j.CompletedAt,
        j.HeartbeatAt,
        j.Attempt,
        j.ResultMessage,
        j.ErrorCode,
        j.HyperVCode,
        j.CorrelationId,
        cancellable = j.Status == JobStatus.Queued || (j.Status == JobStatus.Running && j.DispatchedAt == null),
    };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/jobs").WithTags("Jobs");

        g.MapGet("/", async (HttpContext http, PortalDbContext db, AccessEvaluator access, string? status, int? take) =>
        {
            var uid = http.User.UserId();
            var all = await access.HasAsync(uid, Permissions.JobsReadAll, ResourceRef.Global, http.RequestAborted);
            var q = db.Jobs.AsNoTracking().Where(j => all || j.RequestedByUserId == uid);
            if (Enum.TryParse<JobStatus>(status, true, out var s))
            {
                q = q.Where(j => j.Status == s);
            }
            var list = await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync(http.RequestAborted);
            var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName, http.RequestAborted);
            return Results.Ok(list.Select(j => new { job = Dto(j), requestedBy = names.GetValueOrDefault(j.RequestedByUserId) }));
        });

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access) =>
        {
            var job = await VisibleJobAsync(id, http, db, access);
            if (job is null)
            {
                return ApiSupport.NotFound();
            }
            var events = await db.JobEvents.AsNoTracking().Where(e => e.JobId == id).OrderBy(e => e.Id).ToListAsync(http.RequestAborted);
            return Results.Ok(new { job = Dto(job), events });
        });

        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, JobService jobs) =>
        {
            var job = await VisibleJobAsync(id, http, db, access);
            if (job is null)
            {
                return ApiSupport.NotFound();
            }
            var uid = http.User.UserId();
            if (job.RequestedByUserId != uid && await http.GuardAsync(Permissions.JobsCancelAll, ResourceRef.Global, "jobs.cancel") is { } denied)
            {
                return denied;
            }
            var result = await jobs.CancelAsync(id, http.RequestAborted);
            await http.WriteAuditAsync("jobs.cancel", resourceType: "job", resourceId: id.ToString("D"), details: new { result = result.ToString() });
            return result switch
            {
                CancelResult.Cancelled or CancelResult.CancelRequested => Results.Ok(new { result = result.ToString() }),
                CancelResult.NotInterruptible => ApiSupport.Conflict("not_interruptible",
                    "The command was already sent to the host and cannot be interrupted safely."),
                _ => ApiSupport.Conflict("already_finished"),
            };
        });
    }

    private static async Task<Job?> VisibleJobAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, http.RequestAborted);
        if (job is null)
        {
            return null;
        }
        var uid = http.User.UserId();
        return job.RequestedByUserId == uid || await access.HasAsync(uid, Permissions.JobsReadAll, ResourceRef.Global, http.RequestAborted) ? job : null;
    }
}
