using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Jobs;

namespace HvPortal.Gateway.Templates;

/// <summary>
/// Rules of VM templates (ADR-028) shared by the API (request time) and the job runner (execution time): where a
/// template lives on the host, what a VM created from it receives, how long the copies may take. Everything the agent
/// is told about a template (folder, library, disk files) comes from the template record, never from a client.
/// </summary>
public static class TemplateRules
{
    private const long Gb = 1L << 30;

    /// <summary>Longest copy the gateway waits for (the agent accepts the same, see CommandExecutor).</summary>
    public const int MaxCopySeconds = 8 * 3600;

    public static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= 100 } && name.Trim().Length == name.Length && !name.Any(char.IsControl);

    /// <summary>Folder of a host acceptable as template library: same rules as the ISO library, system folders excluded.</summary>
    public static bool IsValidLibraryPath(string? path) =>
        OperationPolicy.IsValidLibraryPath(path) && HostPaths.IsValidLocalFolder(path) && !HostPaths.IsBlocked(path!);

    public static string FolderOf(string libraryPath, Guid templateId) => libraryPath.TrimEnd('\\') + "\\" + templateId.ToString("D");

    public static IReadOnlyList<TemplateDisk> Disks(VmTemplate t)
    {
        if (string.IsNullOrEmpty(t.DisksJson))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<TemplateDisk>>(t.DisksJson, AgentProtocol.Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static int GbCeiling(long bytes) => (int)Math.Min(int.MaxValue, (Math.Max(0, bytes) + Gb - 1) / Gb);

    /// <summary>Virtual size of the boot disk (GB, rounded up): the smallest disk a VM created from the template can have.</summary>
    public static int BootDiskGb(VmTemplate t) => Disks(t) is [var first, ..] ? GbCeiling(first.SizeBytes) : 0;

    /// <summary>
    /// The request with everything that is decided by the template: its disks (copied by the agent), its generation,
    /// the size of the other disks for the quotas. The boot disk is never smaller than the template's. Null when the
    /// template has no usable disk list.
    /// </summary>
    public static NewVmRequest? Stamp(NewVmRequest spec, VmTemplate t)
    {
        var disks = Disks(t);
        if (disks.Count == 0 || disks.Any(d => !HostPaths.IsValidFileName(d.File)))
        {
            return null;
        }
        var boot = GbCeiling(disks[0].SizeBytes);
        return spec with
        {
            TemplateId = t.Id,
            Generation = t.Generation,
            TemplateDisks = disks.Select(d => t.FolderPath.TrimEnd('\\') + "\\" + d.File).ToList(),
            TemplateRoots = [t.LibraryPath],
            TemplateExtraDiskGb = disks.Skip(1).Sum(d => GbCeiling(d.SizeBytes)),
            DiskSizeGb = Math.Max(spec.DiskSizeGb, boot),
            DiskFixed = false,
        };
    }

    /// <summary>A request that is not from a template never carries template fields, whatever was sent.</summary>
    public static NewVmRequest WithoutTemplate(NewVmRequest spec) =>
        spec with { TemplateId = null, TemplateDisks = [], TemplateRoots = [], TemplateExtraDiskGb = 0 };

    /// <summary>Time allowed to copy <paramref name="bytes"/>: 15 minutes plus 20 seconds per GB (about 50 MB/s), at most 8 hours.</summary>
    public static int CopyTimeoutSeconds(long bytes) => (int)Math.Min(MaxCopySeconds, 900 + (Math.Max(0, bytes) / Gb + 1) * 20);

    /// <summary>What the job that created or deleted a template means for its record. Called when the job reaches a final state.</summary>
    public static void ApplyJobOutcome(PortalDbContext db, VmTemplate t, Job job, JobStatus status, string? message)
    {
        var note = message is { Length: > 2000 } ? message[..2000] : message;
        if (job.Type == JobService.TemplateCaptureJobType)
        {
            NewTemplateResult? result = null;
            if (status == JobStatus.Succeeded && job.ResultJson is { Length: > 0 } json)
            {
                try
                {
                    result = JsonSerializer.Deserialize<NewTemplateResult>(json, AgentProtocol.Json);
                }
                catch (JsonException)
                {
                    result = null;
                }
            }
            if (result is { Disks.Count: > 0 } && result.Disks.All(d => HostPaths.IsValidFileName(d.File) && d.SizeBytes > 0))
            {
                t.Generation = result.Generation;
                t.ProcessorCount = result.ProcessorCount;
                t.MemoryStartupMb = result.MemoryStartupMb;
                t.DynamicMemory = result.DynamicMemory;
                t.MemoryMinimumMb = result.MemoryMinimumMb;
                t.MemoryMaximumMb = result.MemoryMaximumMb;
                t.SecureBootTemplate = result.SecureBootTemplate;
                t.TpmEnabled = result.TpmEnabled;
                t.DisksJson = JsonSerializer.Serialize(result.Disks, AgentProtocol.Json);
                t.SizeBytes = result.Disks.Sum(d => d.FileBytes);
                t.Status = TemplateStatus.Ready;
                t.Error = null;
            }
            else if (status == JobStatus.Succeeded)
            {
                // Never shown as ready without knowing what was copied.
                t.Status = TemplateStatus.Unverified;
                t.Error = "The host reported success but did not return the list of copied disks.";
            }
            else if (job.DispatchedAt is null)
            {
                // The request never reached the host: nothing exists there.
                db.VmTemplates.Remove(t);
            }
            else
            {
                t.Status = status == JobStatus.Failed ? TemplateStatus.Failed : TemplateStatus.Unverified;
                t.Error = note ?? status.ToString();
            }
        }
        else if (job.Type == JobService.TemplateDeleteJobType)
        {
            if (status == JobStatus.Succeeded)
            {
                db.VmTemplates.Remove(t);
            }
            else if (job.DispatchedAt is null)
            {
                // Nothing was touched on the host: the template is what it was before the request.
                t.Status = t.Error is not null ? TemplateStatus.Unverified : t.DisksJson is not null ? TemplateStatus.Ready : TemplateStatus.Failed;
            }
            else
            {
                t.Status = TemplateStatus.Unverified;
                t.Error = "Deleting the template did not complete: " + (note ?? status.ToString());
            }
        }
        if (db.Entry(t).State != Microsoft.EntityFrameworkCore.EntityState.Deleted)
        {
            t.JobId = null;
        }
    }
}
