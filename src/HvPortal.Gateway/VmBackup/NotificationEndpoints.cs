using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;

namespace HvPortal.Gateway.VmBackup;

/// <summary>Settings > Notifications: SMTP and webhooks (settings.manage). The SMTP password is write-only.</summary>
public static class NotificationEndpoints
{
    public sealed record EmailBody(bool Enabled, string? Host, int Port, string? Security, string? UserName, string? Password, string? From, string[]? To);
    public sealed record Body(EmailBody? Email, NotificationService.Webhook[]? Webhooks, bool RestoreTestFailed, bool AgentsOutdated);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/settings/notifications").WithTags("Settings");
        g.MapGet("/", GetAsync);
        g.MapPut("/", SaveAsync);
        g.MapPost("/test", TestAsync);
    }

    private static object Dto(NotificationService.Settings s) => new
    {
        email = new
        {
            s.Email.Enabled, s.Email.Host, s.Email.Port, s.Email.Security, s.Email.UserName, hasPassword = s.Email.PasswordSealed is not null, s.Email.From, s.Email.To,
        },
        webhooks = s.Webhooks.Select(w => new { w.Name, url = w.Display, w.Format }),
        s.RestoreTestFailed,
        s.AgentsOutdated,
    };

    /// <summary>Webhook addresses often carry a secret in the path or query: only the host and the start of the path are shown back.</summary>
    internal static string MaskUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{(u.AbsolutePath.Length > 12 ? u.AbsolutePath[..12] + "…" : u.AbsolutePath)}" : "?";

    private static async Task<IResult> GetAsync(HttpContext http, PortalDbContext db)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.notifications.read") is { } denied)
        {
            return denied;
        }
        return Results.Ok(Dto(await NotificationService.LoadAsync(db, http.RequestAborted)));
    }

    private static async Task<IResult> SaveAsync(Body body, HttpContext http, PortalDbContext db, NotificationService notifications, TimeProvider time)
    {
        if ((await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.notifications.update") ?? await http.RequireRecentReauthAsync()) is { } denied)
        {
            return denied;
        }
        var current = await NotificationService.LoadAsync(db, http.RequestAborted);
        var e = body.Email ?? new EmailBody(false, null, 587, "starttls", null, null, null, null);
        // Password: null = keep; "" = remove; a value = replace.
        var sealedPassword = e.Password is null ? current.Email.PasswordSealed : e.Password.Length == 0 ? null : notifications.SealPassword(e.Password);
        // Webhooks: an address shown masked and sent back unchanged keeps the stored (sealed) one; a new address is
        // validated, then sealed with the key ring (it carries the secret of the webhook).
        var hooks = new List<NotificationService.Webhook>();
        foreach (var w in body.Webhooks ?? [])
        {
            if (current.Webhooks.FirstOrDefault(old => old.Name == w.Name && old.Display == w.Url) is { } kept)
            {
                hooks.Add(kept with { Format = w.Format });
                continue;
            }
            if (w.Url is null || w.Url.Contains('…', StringComparison.Ordinal))
            {
                return ApiSupport.BadRequest("webhook_url_masked", "Type the full address of the webhook again: the stored one is only shown masked.");
            }
            if (NotificationService.ValidateWebhookUrl(w.Url) is { } invalidUrl)
            {
                return ApiSupport.BadRequest(invalidUrl);
            }
            hooks.Add(notifications.SealWebhook(w with { Mask = null }));
        }
        var settings = new NotificationService.Settings
        {
            Email = new NotificationService.EmailSettings
            {
                Enabled = e.Enabled, Host = e.Host?.Trim(), Port = e.Port, Security = e.Security ?? "starttls", UserName = string.IsNullOrWhiteSpace(e.UserName) ? null : e.UserName.Trim(),
                PasswordSealed = string.IsNullOrWhiteSpace(e.UserName) ? null : sealedPassword, From = e.From?.Trim(),
                To = (e.To ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            },
            Webhooks = hooks,
            RestoreTestFailed = body.RestoreTestFailed,
            AgentsOutdated = body.AgentsOutdated,
        };
        if (NotificationService.Validate(settings) is { } invalid)
        {
            return ApiSupport.BadRequest(invalid);
        }
        await NotificationService.SaveAsync(db, settings, time.GetUtcNow(), http.RequestAborted);
        await http.WriteAuditAsync("settings.notifications.update", details: new
        {
            email = settings.Email.Enabled, settings.Email.Host, recipients = settings.Email.To.Count, webhooks = settings.Webhooks.Select(w => new { w.Name, w.Format }),
            passwordChanged = e.Password is not null,
        });
        return Results.Ok(Dto(settings));
    }

    private static async Task<IResult> TestAsync(HttpContext http, NotificationService notifications)
    {
        if (await http.GuardAsync(Permissions.SettingsManage, ResourceRef.Global, "settings.notifications.test") is { } denied)
        {
            return denied;
        }
        var settings = await notifications.LoadAsync(http.RequestAborted);
        if (!settings.Email.Enabled && settings.Webhooks.Count == 0)
        {
            return ApiSupport.BadRequest("no_channel", "Configure e-mail or a webhook first.");
        }
        var errors = await notifications.SendAsync(new NotificationService.Message("test", "ok", "Test notification",
            "This is a test message from HvPortal. Notifications of backups, restore tests and agent updates arrive here."), http.RequestAborted, settings);
        await http.WriteAuditAsync("settings.notifications.test", errors.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure, details: new { errors });
        return Results.Ok(new { sent = errors.Count == 0, errors });
    }
}
