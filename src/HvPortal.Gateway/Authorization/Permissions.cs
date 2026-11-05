using HvPortal.Gateway.Data;

namespace HvPortal.Gateway.Authorization;

/// <summary>
/// Permission catalog. Each permission declares the scope types where it is meaningful.
/// Platform permissions (users/roles/settings/audit/hosts) are only evaluated at Global scope.
/// Permissions marked "Milestone" are part of the model already so custom roles stay stable,
/// but no endpoint uses them yet.
/// </summary>
public static class Permissions
{
    public const string InventoryRead = "inventory.read";
    public const string InventoryAnnotate = "inventory.annotate";
    public const string VmPower = "vm.power";
    public const string VmPowerForce = "vm.power.force";
    public const string VmCreate = "vm.create";
    public const string GroupsManage = "groups.manage";
    public const string VmConfigCompute = "vm.config.compute";
    public const string VmConfigStorage = "vm.config.storage";
    public const string VmConfigNetwork = "vm.config.network";
    public const string VmCheckpointCreate = "vm.checkpoint.create";
    public const string VmCheckpointApply = "vm.checkpoint.apply";
    public const string VmDelete = "vm.delete";
    public const string VmConsole = "vm.console";
    public const string VmMigrate = "vm.migrate";
    public const string BackupRun = "backup.run";
    public const string BackupRestore = "backup.restore";
    public const string JobsReadAll = "jobs.read.all";
    public const string JobsCancelAll = "jobs.cancel.all";
    public const string HostsManage = "hosts.manage";
    public const string HostFilesRead = "host.files.read";
    public const string HostFilesWrite = "host.files.write";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditRead = "audit.read";

    public sealed record Definition(string Name, string Description, ScopeType[] Scopes, bool Implemented, bool Privileged = false);

    private static readonly ScopeType[] VmScopes = [ScopeType.Global, ScopeType.Cluster, ScopeType.Host, ScopeType.Group, ScopeType.Vm];
    private static readonly ScopeType[] GlobalOnly = [ScopeType.Global];

    public static readonly IReadOnlyList<Definition> All =
    [
        new(InventoryRead, "Read inventory (hosts, VMs, state)", VmScopes, true),
        new(InventoryAnnotate, "Edit portal descriptions of VMs", VmScopes, true),
        new(VmPower, "Start, guest shutdown/restart, pause, resume, save", VmScopes, true),
        new(VmPowerForce, "Turn off and hard reset (may lose guest data)", VmScopes, true),
        new(VmCreate, "Create VMs on a host", [ScopeType.Global, ScopeType.Cluster, ScopeType.Host], true),
        new(VmConfigCompute, "Change general settings, CPU, memory, firmware", VmScopes, true),
        new(VmConfigStorage, "Change disks and DVD/ISO", VmScopes, true),
        new(VmConfigNetwork, "Change network adapters", VmScopes, true),
        new(VmCheckpointCreate, "Create checkpoints", VmScopes, true),
        new(VmCheckpointApply, "Apply or delete checkpoints", VmScopes, true),
        new(VmDelete, "Delete VMs (configuration; disk files are kept)", VmScopes, true),
        new(VmConsole, "Open VM console", VmScopes, true),
        new(VmMigrate, "Migrate VMs and storage", VmScopes, false),
        new(BackupRun, "Run backups", [.. VmScopes, ScopeType.BackupRepository], false),
        new(BackupRestore, "Restore backups", [.. VmScopes, ScopeType.BackupRepository], false),
        new(GroupsManage, "Create, move and delete VM groups and their membership", GlobalOnly, true),
        new(JobsReadAll, "Read jobs of all users", GlobalOnly, true),
        new(JobsCancelAll, "Cancel jobs of all users", GlobalOnly, true),
        new(HostsManage, "Enroll and revoke hosts", GlobalOnly, true, Privileged: true),
        new(HostFilesRead, "Browse host folders and download files (system folders excluded)", [ScopeType.Global, ScopeType.Cluster, ScopeType.Host], true),
        new(HostFilesWrite, "Upload files to host folders (system folders excluded)", [ScopeType.Global, ScopeType.Cluster, ScopeType.Host], true),
        new(UsersManage, "Manage users", GlobalOnly, true, Privileged: true),
        new(RolesManage, "Manage roles and role assignments", GlobalOnly, true, Privileged: true),
        new(SettingsManage, "Change security settings", GlobalOnly, true, Privileged: true),
        new(AuditRead, "Read and export the security audit log", GlobalOnly, true),
    ];

    public static readonly IReadOnlySet<string> Names = All.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> PrivilegedNames = All.Where(d => d.Privileged).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

    public static Definition? Find(string name) => All.FirstOrDefault(d => d.Name == name);
}

public static class BuiltInRoles
{
    public const string PlatformAdmin = "platform-admin";
    public const string InfrastructureAdmin = "infrastructure-admin";
    public const string VmOperator = "vm-operator";
    public const string BackupOperator = "backup-operator";
    public const string Auditor = "auditor";
    public const string ReadOnly = "read-only";

    public sealed record Definition(string Key, string Name, string Description, string[] Permissions);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(PlatformAdmin, "Platform administrator", "Full control including users, roles and security settings",
            [.. Authorization.Permissions.Names]),
        new(InfrastructureAdmin, "Infrastructure administrator", "Manage hosts and all VM operations; no user/role/security management",
            Authorization.Permissions.All.Where(p => p.Name is not (Authorization.Permissions.UsersManage or Authorization.Permissions.RolesManage or Authorization.Permissions.SettingsManage))
                .Select(p => p.Name).ToArray()),
        new(VmOperator, "VM operator", "Day-to-day VM operations",
            [Authorization.Permissions.InventoryRead, Authorization.Permissions.InventoryAnnotate, Authorization.Permissions.VmPower,
             Authorization.Permissions.VmConsole, Authorization.Permissions.VmCheckpointCreate]),
        new(BackupOperator, "Backup operator", "Run backups and restores",
            [Authorization.Permissions.InventoryRead, Authorization.Permissions.BackupRun, Authorization.Permissions.BackupRestore]),
        new(Auditor, "Auditor", "Read inventory, all jobs and the audit log",
            [Authorization.Permissions.InventoryRead, Authorization.Permissions.AuditRead, Authorization.Permissions.JobsReadAll]),
        new(ReadOnly, "Read only", "Read inventory",
            [Authorization.Permissions.InventoryRead]),
    ];
}
