using System.Text.Json;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Api;

/// <summary>
/// Users, roles, role assignments, security settings, audit.
/// Anti-escalation rules (enforced here, tested in AuthorizationEscalationTests):
///  - nobody can change their own role assignments or disable/reset themselves through admin endpoints;
///  - granting a role (or removing a deny) at a scope requires the actor to hold every permission of that role at that scope;
///  - adding permissions to a custom role requires the actor to hold them globally;
///  - managing a privileged user (password/MFA reset, disable) requires the actor to hold every privileged permission;
///  - the last enabled global platform administrator cannot be removed or disabled.
/// </summary>
public static class AdminEndpoints
{
    /// <param name="AuthSource">"local" (default, temporary password) or "entra" (Microsoft account of the configured tenant, no password).</param>
    /// <param name="RequireMfa">Local users: enroll an authenticator at first sign-in (default true).</param>
    /// <param name="RoleId">Optional role assigned at creation (needs roles.manage and the role's permissions on the scope).</param>
    public sealed record CreateUserRequest(string UserName, string? DisplayName, string? AuthSource = null, bool? RequireMfa = null,
        Guid? RoleId = null, ScopeType? ScopeType = null, string? ScopeId = null);
    public sealed record RoleRequest(string Name, string? Description, string[] Permissions);
    public sealed record AssignmentRequest(Guid UserId, Guid RoleId, ScopeType ScopeType, string? ScopeId, AssignmentEffect Effect);

    public static void Map(WebApplication app)
    {
        var users = app.MapGroup("/api/v1/users").WithTags("Users");
        users.MapGet("/", ListUsersAsync);
        users.MapPost("/", CreateUserAsync);
        users.MapPost("/{id:guid}/disable", (Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, SessionService s) => SetEnabledAsync(id, false, http, db, access, s));
        users.MapPost("/{id:guid}/enable", (Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, SessionService s) => SetEnabledAsync(id, true, http, db, access, s));
        users.MapPost("/{id:guid}/reset-password", ResetPasswordAsync);
        users.MapPost("/{id:guid}/reset-mfa", ResetMfaAsync);
        users.MapPost("/{id:guid}/delete", DeleteUserAsync);

        var roles = app.MapGroup("/api/v1").WithTags("Roles");
        roles.MapGet("/permissions", () => Results.Ok(Permissions.All.Select(p => new
        {
            p.Name, p.Description, scopes = p.Scopes.Select(s => s.ToString()), p.Implemented, p.Privileged,
        })));
        roles.MapGet("/roles", ListRolesAsync);
        roles.MapPost("/roles", CreateRoleAsync);
        roles.MapPut("/roles/{id:guid}", UpdateRoleAsync);
        roles.MapDelete("/roles/{id:guid}", DeleteRoleAsync);
        roles.MapGet("/role-assignments", ListAssignmentsAsync);
        roles.MapPost("/role-assignments", CreateAssignmentAsync);
        roles.MapDelete("/role-assignments/{id:guid}", DeleteAssignmentAsync);

        var settings = app.MapGroup("/api/v1/settings").WithTags("Settings");
        settings.MapGet("/security", async (HttpContext http, SettingsStore store) =>
            await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.read") ?? Results.Ok(await store.GetSecurityAsync(http.RequestAborted)));
        settings.MapPut("/security", UpdateSecurityAsync);

        var audit = app.MapGroup("/api/v1/audit").WithTags("Audit");
        audit.MapGet("/", ListAuditAsync);
        audit.MapGet("/export", ExportAuditAsync);
        audit.MapPost("/verify", async (HttpContext http, AuditLog log) =>
            await http.GuardAsync(Permissions.AuditRead, ResourceRef.Global, "audit.verify") ?? Results.Ok(await log.VerifyAsync(http.RequestAborted)));
    }

    // ------------------------------------------------------------------ users

    private static async Task<IResult> ListUsersAsync(HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.UsersManage, ResourceRef.Global, "users.list") is { } denied)
        {
            return denied;
        }
        var list = await db.Users.AsNoTracking().OrderBy(u => u.UserName).Select(u => new
        {
            u.Id, u.UserName, u.DisplayName, u.AuthSource, u.IsEnabled, mfaEnabled = u.TotpEnabled, u.MustChangePassword, u.MfaEnrollmentRequired,
            entraBound = u.ExternalObjectId != null,
            u.IsBreakGlass, u.CreatedAt, u.LastLoginAt,
        }).ToListAsync(http.RequestAborted);
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateUserAsync(CreateUserRequest req, HttpContext http, PortalDbContext db, PasswordHasher hasher, TimeProvider time,
        AccessEvaluator access, HvPortal.Gateway.Security.Entra.EntraConfigStore entra)
    {
        if ((await http.GuardAsync(Permissions.UsersManage, ResourceRef.Global, "users.create") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var source = req.AuthSource ?? "local";
        if (source is not ("local" or "entra"))
        {
            return ApiSupport.BadRequest("invalid_auth_source");
        }
        if (req.UserName is null || !ApiSupport.UserNameRegex().IsMatch(req.UserName))
        {
            return ApiSupport.BadRequest("invalid_user_name", "3-64 characters: letters, digits, '.', '_', '-', '@'.");
        }
        if (source == "entra" && !req.UserName.Contains('@', StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("invalid_user_name", "Microsoft accounts are identified by their user principal name (name@contoso.com).");
        }
        if (req.DisplayName is { Length: > 0 } dn && !ApiSupport.DisplayNameRegex().IsMatch(dn))
        {
            return ApiSupport.BadRequest("invalid_display_name");
        }
        var normalized = ApiSupport.NormalizeUserName(req.UserName);
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalized, http.RequestAborted))
        {
            return ApiSupport.Conflict("user_exists");
        }
        string? tenant = null;
        if (source == "entra")
        {
            var cfg = await entra.GetAsync(http.RequestAborted);
            if (!cfg.IsComplete)
            {
                return ApiSupport.BadRequest("entra_not_configured", "Configure Microsoft Entra ID in Settings first.");
            }
            tenant = cfg.TenantId;
        }

        // Optional role at creation: same rules as POST /role-assignments (roles.manage, no permission amplification).
        Role? role = null;
        ResourceRef? scope = null;
        var actor = http.User.UserId();
        if (req.RoleId is { } roleId)
        {
            if (await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "role_assignments.create") is { } noRoles)
            {
                return noRoles;
            }
            role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, http.RequestAborted);
            if (role is null)
            {
                return ApiSupport.BadRequest("unknown_role");
            }
            scope = await ResolveScopeAsync(req.ScopeType ?? ScopeType.Global, req.ScopeId, db, http.RequestAborted);
            if (scope is null)
            {
                return ApiSupport.BadRequest("invalid_scope", "Supported scopes in this version: Global, Host, Vm, Group (existing resources only).");
            }
            if (!await access.HoldsAllAsync(actor, role.Permissions.Select(p => p.Permission), scope.Value, http.RequestAborted))
            {
                await http.WriteAuditAsync("users.create", AuditOutcome.Denied, "user", null,
                    new { req.UserName, reason = "permission amplification", role = role.Name, scope = scope.Value.ToString() });
                return ApiSupport.Forbidden("permission_amplification", "You can only grant roles whose permissions you hold on that scope.");
            }
        }

        var temp = source == "local" ? ApiSupport.TemporaryPassword() : null;
        var user = new User
        {
            UserName = req.UserName,
            NormalizedUserName = normalized,
            DisplayName = req.DisplayName,
            AuthSource = source,
            ExternalTenantId = tenant,
            PasswordHash = temp is null ? null : hasher.Hash(temp),
            MustChangePassword = temp is not null,
            MfaEnrollmentRequired = source == "local" && req.RequireMfa != false,
            CreatedAt = time.GetUtcNow(),
        };
        db.Users.Add(user);
        if (role is not null)
        {
            db.RoleAssignments.Add(new RoleAssignment
            {
                PrincipalId = user.Id.ToString("D"),
                RoleId = role.Id,
                ScopeType = scope!.Value.Type,
                ScopeId = scope.Value.Id,
                Effect = AssignmentEffect.Allow,
                CreatedAt = time.GetUtcNow(),
                CreatedByUserId = actor,
            });
        }
        await db.SaveChangesAsync(http.RequestAborted);
        access.Invalidate();
        await http.WriteAuditAsync("users.create", resourceType: "user", resourceId: user.Id.ToString("D"),
            details: new { user.UserName, source, mfaAtFirstSignIn = user.MfaEnrollmentRequired, role = role?.Name, scope = scope?.ToString() });
        if (role is not null)
        {
            await http.WriteAuditAsync("role_assignments.create", resourceType: "user", resourceId: user.Id.ToString("D"),
                details: new { user = user.UserName, role = role.Name, scope = scope!.Value.ToString(), effect = nameof(AssignmentEffect.Allow) });
        }
        // Without a role the user has no permission (default deny) until one is assigned.
        return Results.Ok(new
        {
            user.Id,
            user.UserName,
            authSource = source,
            temporaryPassword = temp,
            note = temp is not null
                ? "Shown once. The user must change it at first sign-in" + (user.MfaEnrollmentRequired ? " and then set up an authenticator app." : ".")
                : "The user signs in with Microsoft (Entra ID); no password is stored in the portal.",
        });
    }

    private static async Task<IResult?> GuardTargetAsync(HttpContext http, AccessEvaluator access, Guid targetId, string action)
    {
        if ((await http.GuardAsync(Permissions.UsersManage, ResourceRef.Global, action) ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var actor = http.User.UserId();
        if (actor == targetId)
        {
            return ApiSupport.Forbidden("self_management_forbidden", "Use the account page to manage your own account.");
        }
        if (await access.IsPrivilegedAsync(targetId, http.RequestAborted)
            && !await access.HoldsAllAsync(actor, Permissions.PrivilegedNames, ResourceRef.Global, http.RequestAborted))
        {
            await http.WriteAuditAsync(action, AuditOutcome.Denied, "user", targetId.ToString("D"), new { reason = "target is more privileged" });
            return ApiSupport.Forbidden("target_more_privileged");
        }
        return null;
    }

    private static async Task<IResult> SetEnabledAsync(Guid id, bool enabled, HttpContext http, PortalDbContext db, AccessEvaluator access, SessionService sessions)
    {
        var action = enabled ? "users.enable" : "users.disable";
        if (await GuardTargetAsync(http, access, id, action) is { } denied)
        {
            return denied;
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, http.RequestAborted);
        if (user is null)
        {
            return ApiSupport.NotFound();
        }
        if (!enabled && await IsLastPlatformAdminAsync(db, id, null, http.RequestAborted))
        {
            return ApiSupport.Conflict("last_platform_admin");
        }
        user.IsEnabled = enabled;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(http.RequestAborted);
        if (!enabled)
        {
            await sessions.RevokeAllAsync(id, "user disabled", null, http.RequestAborted);
        }
        await http.WriteAuditAsync(action, resourceType: "user", resourceId: id.ToString("D"));
        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access,
        PasswordHasher hasher, SessionService sessions, TimeProvider time)
    {
        if (await GuardTargetAsync(http, access, id, "users.reset_password") is { } denied)
        {
            return denied;
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.AuthSource == "local", http.RequestAborted);
        if (user is null)
        {
            return ApiSupport.NotFound();
        }
        var temp = ApiSupport.TemporaryPassword();
        user.PasswordHash = hasher.Hash(temp);
        user.MustChangePassword = true;
        user.PasswordChangedAt = time.GetUtcNow();
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(http.RequestAborted);
        var revoked = await sessions.RevokeAllAsync(id, "password reset by administrator", null, http.RequestAborted);
        await http.WriteAuditAsync("users.reset_password", resourceType: "user", resourceId: id.ToString("D"), details: new { sessionsRevoked = revoked });
        return Results.Ok(new { temporaryPassword = temp, note = "Shown once. All sessions of the user were revoked." });
    }

    private static async Task<IResult> ResetMfaAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access, SessionService sessions)
    {
        if (await GuardTargetAsync(http, access, id, "users.reset_mfa") is { } denied)
        {
            return denied;
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, http.RequestAborted);
        if (user is null)
        {
            return ApiSupport.NotFound();
        }
        user.TotpEnabled = false;
        user.TotpSecretEncrypted = null;
        user.PendingTotpSecretEncrypted = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.RecoveryCodes.Where(r => r.UserId == id).ExecuteDeleteAsync(http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        await sessions.RevokeAllAsync(id, "mfa reset by administrator", null, http.RequestAborted);
        await http.WriteAuditAsync("users.reset_mfa", resourceType: "user", resourceId: id.ToString("D"));
        return Results.NoContent();
    }

    public sealed record DeleteUserRequest(string? ConfirmName);

    /// <summary>
    /// Permanently removes a user: sessions, MFA secrets, recovery codes, pending tokens and every role assignment.
    /// The audit log keeps the actor id and name of past actions; queued jobs of the user fail at execution
    /// (authorization_revoked). Not allowed on yourself, on a more privileged user or on the last platform administrator.
    /// </summary>
    private static async Task<IResult> DeleteUserAsync(Guid id, DeleteUserRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if (await GuardTargetAsync(http, access, id, "users.delete") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiSupport.NotFound();
        }
        if (!string.Equals(req.ConfirmName, user.UserName, StringComparison.Ordinal))
        {
            return ApiSupport.BadRequest("confirmation_required", "Type the user name exactly to confirm.");
        }
        if (user.IsEnabled && await IsLastPlatformAdminAsync(db, id, null, ct))
        {
            return ApiSupport.Conflict("last_platform_admin");
        }
        var principal = id.ToString("D");
        var roles = await db.RoleAssignments.AsNoTracking().Where(a => a.PrincipalType == "user" && a.PrincipalId == principal)
            .Select(a => new { role = a.Role!.Name, scopeType = a.ScopeType.ToString(), a.ScopeId, effect = a.Effect.ToString() }).ToListAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Sessions.Where(s => s.UserId == id).ExecuteDeleteAsync(ct);
            await db.RecoveryCodes.Where(r => r.UserId == id).ExecuteDeleteAsync(ct);
            await db.MfaChallenges.Where(m => m.UserId == id).ExecuteDeleteAsync(ct);
            await db.OneTimeTokens.Where(t => t.UserId == id).ExecuteDeleteAsync(ct);
            await db.RoleAssignments.Where(a => a.PrincipalType == "user" && a.PrincipalId == principal).ExecuteDeleteAsync(ct);
            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        await http.WriteAuditAsync("users.delete", resourceType: "user", resourceId: principal,
            details: new { user.UserName, user.AuthSource, user.IsBreakGlass, assignmentsRemoved = roles });
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ roles

    private static async Task<IResult> ListRolesAsync(HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "roles.list") is { } denied)
        {
            return denied;
        }
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(http.RequestAborted);
        return Results.Ok(roles.Select(r => new { r.Id, r.Name, r.Description, r.IsBuiltIn, r.BuiltInKey, permissions = r.Permissions.Select(p => p.Permission).Order() }));
    }

    private static async Task<IResult?> ValidateRoleAsync(RoleRequest req, HttpContext http, AccessEvaluator access)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || !ApiSupport.DisplayNameRegex().IsMatch(req.Name))
        {
            return ApiSupport.BadRequest("invalid_role_name");
        }
        var unknown = (req.Permissions ?? []).Where(p => !Permissions.Names.Contains(p)).ToList();
        if (unknown.Count > 0)
        {
            return ApiSupport.BadRequest("unknown_permission", string.Join(", ", unknown));
        }
        if (!await access.HoldsAllAsync(http.User.UserId(), req.Permissions ?? [], ResourceRef.Global, http.RequestAborted))
        {
            await http.WriteAuditAsync("roles.write", AuditOutcome.Denied, details: new { reason = "permission amplification", req.Name });
            return ApiSupport.Forbidden("permission_amplification", "You can only include permissions you hold globally.");
        }
        return null;
    }

    private static async Task<IResult> CreateRoleAsync(RoleRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if ((await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "roles.create") ?? await http.RequireRecentReauthAsync()
            ?? await ValidateRoleAsync(req, http, access)) is { } denied)
        {
            return denied;
        }
        if (await db.Roles.AnyAsync(r => r.Name == req.Name, http.RequestAborted))
        {
            return ApiSupport.Conflict("role_exists");
        }
        var role = new Role { Name = req.Name, Description = req.Description };
        role.Permissions.AddRange(req.Permissions.Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("roles.create", resourceType: "role", resourceId: role.Id.ToString("D"), details: new { role.Name, req.Permissions });
        return Results.Ok(new { role.Id });
    }

    private static async Task<IResult> UpdateRoleAsync(Guid id, RoleRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if ((await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "roles.update") ?? await http.RequireRecentReauthAsync()
            ?? await ValidateRoleAsync(req, http, access)) is { } denied)
        {
            return denied;
        }
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (role is null)
        {
            return ApiSupport.NotFound();
        }
        if (role.IsBuiltIn)
        {
            return ApiSupport.Conflict("builtin_role_immutable");
        }
        var before = role.Permissions.Select(p => p.Permission).Order().ToArray();
        role.Name = req.Name;
        role.Description = req.Description;
        role.Permissions.Clear();
        role.Permissions.AddRange(req.Permissions.Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("roles.update", resourceType: "role", resourceId: id.ToString("D"), details: new { before, after = req.Permissions });
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteRoleAsync(Guid id, HttpContext http, PortalDbContext db)
    {
        if ((await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "roles.delete") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, http.RequestAborted);
        if (role is null)
        {
            return ApiSupport.NotFound();
        }
        if (role.IsBuiltIn)
        {
            return ApiSupport.Conflict("builtin_role_immutable");
        }
        if (await db.RoleAssignments.AnyAsync(a => a.RoleId == id, http.RequestAborted))
        {
            return ApiSupport.Conflict("role_in_use");
        }
        db.Roles.Remove(role);
        await db.SaveChangesAsync(http.RequestAborted);
        await http.WriteAuditAsync("roles.delete", resourceType: "role", resourceId: id.ToString("D"), details: new { role.Name });
        return Results.NoContent();
    }

    private static async Task<IResult> ListAssignmentsAsync(Guid? userId, HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "role_assignments.list") is { } denied)
        {
            return denied;
        }
        var q = db.RoleAssignments.AsNoTracking().Include(a => a.Role).AsQueryable();
        if (userId is { } u)
        {
            var pid = u.ToString("D");
            q = q.Where(a => a.PrincipalId == pid);
        }
        var list = await q.ToListAsync(http.RequestAborted);
        var userNames = await db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString("D"), x => x.UserName, http.RequestAborted);
        var hostNames = await db.Hosts.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString("D"), x => x.DisplayName, http.RequestAborted);
        var vmNames = await db.VirtualMachines.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString("D"), x => x.Name, http.RequestAborted);
        var groupNames = await db.LogicalGroups.AsNoTracking().ToDictionaryAsync(x => x.Id.ToString("D"), x => x.Name, http.RequestAborted);
        return Results.Ok(list.OrderBy(a => a.PrincipalId).Select(a => new
        {
            a.Id,
            userId = a.PrincipalId,
            userName = userNames.GetValueOrDefault(a.PrincipalId),
            a.RoleId,
            roleName = a.Role?.Name,
            scopeType = a.ScopeType.ToString(),
            a.ScopeId,
            scopeName = a.ScopeType switch
            {
                ScopeType.Global => "Global",
                ScopeType.Host => hostNames.GetValueOrDefault(a.ScopeId),
                ScopeType.Vm => vmNames.GetValueOrDefault(a.ScopeId),
                ScopeType.Group => groupNames.GetValueOrDefault(a.ScopeId),
                _ => a.ScopeId,
            },
            effect = a.Effect.ToString(),
            a.CreatedAt,
        }));
    }

    private static async Task<IResult> CreateAssignmentAsync(AssignmentRequest req, HttpContext http, PortalDbContext db, AccessEvaluator access, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "role_assignments.create") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var actor = http.User.UserId();
        if (req.UserId == actor)
        {
            await http.WriteAuditAsync("role_assignments.create", AuditOutcome.Denied, "user", actor.ToString("D"), new { reason = "self assignment" });
            return ApiSupport.Forbidden("self_assignment_forbidden");
        }
        if (!await db.Users.AnyAsync(u => u.Id == req.UserId, http.RequestAborted))
        {
            return ApiSupport.BadRequest("unknown_user");
        }
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == req.RoleId, http.RequestAborted);
        if (role is null)
        {
            return ApiSupport.BadRequest("unknown_role");
        }
        var scope = await ResolveScopeAsync(req.ScopeType, req.ScopeId, db, http.RequestAborted);
        if (scope is null)
        {
            return ApiSupport.BadRequest("invalid_scope", "Supported scopes in this version: Global, Host, Vm, Group (existing resources only).");
        }
        if (!Enum.IsDefined(req.Effect))
        {
            return ApiSupport.BadRequest("invalid_effect");
        }
        if (req.Effect == AssignmentEffect.Allow
            && !await access.HoldsAllAsync(actor, role.Permissions.Select(p => p.Permission), scope.Value, http.RequestAborted))
        {
            await http.WriteAuditAsync("role_assignments.create", AuditOutcome.Denied, "user", req.UserId.ToString("D"),
                new { reason = "permission amplification", role = role.Name, scope = scope.Value.ToString() });
            return ApiSupport.Forbidden("permission_amplification", "You can only grant roles whose permissions you hold on that scope.");
        }
        var pid = req.UserId.ToString("D");
        if (await db.RoleAssignments.AnyAsync(a => a.PrincipalId == pid && a.RoleId == role.Id && a.ScopeType == scope.Value.Type
                                                   && a.ScopeId == scope.Value.Id && a.Effect == req.Effect, http.RequestAborted))
        {
            return ApiSupport.Conflict("assignment_exists");
        }
        var a = new RoleAssignment
        {
            PrincipalId = pid,
            RoleId = role.Id,
            ScopeType = scope.Value.Type,
            ScopeId = scope.Value.Id,
            Effect = req.Effect,
            CreatedAt = time.GetUtcNow(),
            CreatedByUserId = actor,
        };
        db.RoleAssignments.Add(a);
        await db.SaveChangesAsync(http.RequestAborted);
        // The user name is recorded too: the audit must stay readable after the user is deleted.
        var userName = await db.Users.AsNoTracking().Where(u => u.Id == req.UserId).Select(u => u.UserName).FirstOrDefaultAsync(http.RequestAborted);
        await http.WriteAuditAsync("role_assignments.create", resourceType: "user", resourceId: pid,
            details: new { user = userName, role = role.Name, scope = scope.Value.ToString(), effect = req.Effect.ToString() });
        return Results.Ok(new { a.Id });
    }

    private static async Task<IResult> DeleteAssignmentAsync(Guid id, HttpContext http, PortalDbContext db, AccessEvaluator access)
    {
        if ((await http.GuardAsync(Permissions.RolesManage, ResourceRef.Global, "role_assignments.delete") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var a = await db.RoleAssignments.Include(x => x.Role).ThenInclude(r => r!.Permissions).FirstOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (a is null)
        {
            return ApiSupport.NotFound();
        }
        var actor = http.User.UserId();
        if (a.PrincipalId == actor.ToString("D"))
        {
            return ApiSupport.Forbidden("self_assignment_forbidden");
        }
        var scope = new ResourceRef(a.ScopeType, a.ScopeId);
        if (a.Effect == AssignmentEffect.Deny
            && !await access.HoldsAllAsync(actor, a.Role!.Permissions.Select(p => p.Permission), scope, http.RequestAborted))
        {
            return ApiSupport.Forbidden("permission_amplification", "Removing this deny would grant permissions you do not hold.");
        }
        if (a.Effect == AssignmentEffect.Allow && a.Role?.BuiltInKey == BuiltInRoles.PlatformAdmin && a.ScopeType == ScopeType.Global
            && Guid.TryParse(a.PrincipalId, out var uid) && await IsLastPlatformAdminAsync(db, uid, a.Id, http.RequestAborted))
        {
            return ApiSupport.Conflict("last_platform_admin");
        }
        db.RoleAssignments.Remove(a);
        await db.SaveChangesAsync(http.RequestAborted);
        var principalName = Guid.TryParse(a.PrincipalId, out var principal)
            ? await db.Users.AsNoTracking().Where(u => u.Id == principal).Select(u => u.UserName).FirstOrDefaultAsync(http.RequestAborted) : null;
        await http.WriteAuditAsync("role_assignments.delete", resourceType: "user", resourceId: a.PrincipalId,
            details: new { user = principalName, role = a.Role?.Name, scope = scope.ToString(), effect = a.Effect.ToString() });
        return Results.NoContent();
    }

    private static async Task<ResourceRef?> ResolveScopeAsync(ScopeType type, string? id, PortalDbContext db, CancellationToken ct)
    {
        switch (type)
        {
            case ScopeType.Global:
                return ResourceRef.Global;
            case ScopeType.Host when Guid.TryParse(id, out var h) && await db.Hosts.AnyAsync(x => x.Id == h, ct):
                return ResourceRef.Host(h);
            case ScopeType.Vm when Guid.TryParse(id, out var v) && await db.VirtualMachines.AnyAsync(x => x.Id == v, ct):
                return ResourceRef.Vm(v);
            case ScopeType.Group when Guid.TryParse(id, out var g) && await db.LogicalGroups.AnyAsync(x => x.Id == g, ct):
                return ResourceRef.Group(g);
            default:
                return null;
        }
    }

    /// <summary>True when <paramref name="userId"/> is the only enabled user with a global platform-admin Allow (ignoring <paramref name="excludingAssignment"/>).</summary>
    private static async Task<bool> IsLastPlatformAdminAsync(PortalDbContext db, Guid userId, Guid? excludingAssignment, CancellationToken ct)
    {
        var role = await db.Roles.AsNoTracking().FirstAsync(r => r.BuiltInKey == BuiltInRoles.PlatformAdmin, ct);
        var holders = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.RoleId == role.Id && a.ScopeType == ScopeType.Global && a.Effect == AssignmentEffect.Allow && a.Id != excludingAssignment)
            .Select(a => a.PrincipalId).Distinct().ToListAsync(ct);
        var enabled = await db.Users.AsNoTracking().Where(u => u.IsEnabled).Select(u => u.Id.ToString()).ToListAsync(ct);
        var others = holders.Where(h => h != userId.ToString("D") && enabled.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
        var targetHolds = holders.Contains(userId.ToString("D")) || excludingAssignment is not null;
        return targetHolds && others.Count == 0;
    }

    // ------------------------------------------------------------------ settings & audit

    private static async Task<IResult> UpdateSecurityAsync(SecuritySettings req, HttpContext http, SettingsStore store)
    {
        if ((await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.update") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        if (!Enum.IsDefined(req.MfaRequiredFor))
        {
            return ApiSupport.BadRequest("invalid_settings");
        }
        var before = await store.GetSecurityAsync(http.RequestAborted);
        var after = req.Normalize();
        await store.SaveSecurityAsync(after, http.RequestAborted);
        await http.WriteAuditAsync("settings.security.update", details: new { before, after });
        return Results.Ok(after);
    }

    /// <summary>
    /// Audit entries, newest first. Filters: action prefix (or prefixes separated by commas), outcome, period (from/to, UTC) and free text, which matches
    /// the stored fields and also the current names of hosts, VMs, users and groups. Each entry carries the name of
    /// its resource when it can be told (current name, or the name recorded in the details if it was removed), so the
    /// page does not have to show identifiers.
    /// </summary>
    private static async Task<IResult> ListAuditAsync(HttpContext http, PortalDbContext db, int? skip, int? take, string? action, string? outcome,
        string? q, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (await http.GuardAsync(Permissions.AuditRead, ResourceRef.Global, "audit.list") is { } denied)
        {
            return denied;
        }
        var ct = http.RequestAborted;
        var query = db.AuditEntries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
        {
            // One prefix, or several separated by commas (an area of the page covers more than one family of actions).
            var prefixes = action.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray();
            query = prefixes.Length == 1
                ? query.Where(e => e.Action.StartsWith(prefixes[0]))
                : query.Where(e => prefixes.Any(p => e.Action.StartsWith(p)));
        }
        if (Enum.TryParse<AuditOutcome>(outcome, true, out var o))
        {
            query = query.Where(e => e.Outcome == o);
        }
        if (from is { } f)
        {
            var fromUtc = f.ToUniversalTime();
            query = query.Where(e => e.TimestampUtc >= fromUtc);
        }
        if (to is { } t)
        {
            var toUtc = t.ToUniversalTime();
            query = query.Where(e => e.TimestampUtc < toUtc);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim();
            text = text.Length > 100 ? text[..100] : text;
            // Resources whose current name matches: their entries only store the identifier.
            var ids = new List<string>();
            ids.AddRange((await db.Hosts.AsNoTracking().Where(h => h.DisplayName.Contains(text)).Select(h => h.Id).Take(50).ToListAsync(ct)).Select(i => i.ToString("D")));
            ids.AddRange((await db.VirtualMachines.AsNoTracking().Where(v => v.Name.Contains(text)).Select(v => v.Id).Take(100).ToListAsync(ct)).Select(i => i.ToString("D")));
            ids.AddRange((await db.Users.AsNoTracking().Where(u => u.UserName.Contains(text)).Select(u => u.Id).Take(50).ToListAsync(ct)).Select(i => i.ToString("D")));
            ids.AddRange((await db.LogicalGroups.AsNoTracking().Where(g => g.Name.Contains(text)).Select(g => g.Id).Take(50).ToListAsync(ct)).Select(i => i.ToString("D")));
            var correlation = Guid.TryParse(text, out var parsed) ? parsed : (Guid?)null;
            query = query.Where(e => e.Action.Contains(text) || (e.ActorName != null && e.ActorName.Contains(text)) || (correlation != null && e.CorrelationId == correlation)
                || (e.ResourceId != null && (e.ResourceId.Contains(text) || ids.Contains(e.ResourceId)))
                || (e.SourceIp != null && e.SourceIp.Contains(text)) || (e.DetailsJson != null && e.DetailsJson.Contains(text)));
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.Id).Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, 500)).ToListAsync(ct);

        List<Guid> Ids(string type) => items.Where(e => e.ResourceType == type && Guid.TryParse(e.ResourceId, out _)).Select(e => Guid.Parse(e.ResourceId!)).Distinct().ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hostIds = Ids("host");
        var vmIds = Ids("vm");
        var userIds = Ids("user");
        var groupIds = Ids("group");
        foreach (var h in await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).Select(h => new { h.Id, Name = h.DisplayName }).ToListAsync(ct)) names[$"host:{h.Id:D}"] = h.Name;
        foreach (var v in await db.VirtualMachines.AsNoTracking().Where(v => vmIds.Contains(v.Id)).Select(v => new { v.Id, v.Name }).ToListAsync(ct)) names[$"vm:{v.Id:D}"] = v.Name;
        foreach (var u in await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, Name = u.UserName }).ToListAsync(ct)) names[$"user:{u.Id:D}"] = u.Name;
        foreach (var g in await db.LogicalGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => new { g.Id, g.Name }).ToListAsync(ct)) names[$"group:{g.Id:D}"] = g.Name;

        return Results.Ok(new
        {
            total,
            items = items.Select(e =>
            {
                var live = e.ResourceType is not null && e.ResourceId is not null && names.TryGetValue($"{e.ResourceType}:{e.ResourceId}", out var name) ? name : null;
                return new
                {
                    e.Id, e.TimestampUtc, e.ActorUserId, e.ActorName, e.AuthMethod, e.SourceIp, e.Action, e.ResourceType, e.ResourceId, e.Outcome, e.CorrelationId, e.DetailsJson,
                    // resourceExists false = the name comes from the entry itself (the resource is no longer in the portal).
                    resourceName = live ?? RecordedName(e.DetailsJson),
                    resourceExists = live is not null,
                };
            }),
        });
    }

    private static readonly string[] NameProperties = ["vm", "user", "DisplayName", "UserName", "Name"];

    /// <summary>The name written in the details of an entry (also inside "request" / "after"), for resources that no longer exist.</summary>
    private static string? RecordedName(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return Find(doc.RootElement) ?? Nested("request") ?? Nested("after");

            string? Nested(string property) =>
                doc.RootElement.ValueKind == JsonValueKind.Object && TryGet(doc.RootElement, property, out var inner) ? Find(inner) : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static bool TryGet(JsonElement obj, string property, out JsonElement value)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        static string? Find(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (var property in NameProperties)
            {
                if (TryGet(obj, property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } name)
                {
                    return name.Length > 200 ? name[..200] : name;
                }
            }
            return null;
        }
    }

    private static async Task ExportAuditAsync(HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.AuditRead, ResourceRef.Global, "audit.export") is { } denied)
        {
            await denied.ExecuteAsync(http);
            return;
        }
        await http.WriteAuditAsync("audit.export");
        http.Response.ContentType = "application/x-ndjson";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"hvportal-audit-{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl\"";
        await foreach (var e in db.AuditEntries.AsNoTracking().OrderBy(e => e.Id).AsAsyncEnumerable().WithCancellation(http.RequestAborted))
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(e) + "\n", http.RequestAborted);
        }
    }
}
