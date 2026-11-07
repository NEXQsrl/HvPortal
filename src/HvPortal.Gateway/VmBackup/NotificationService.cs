using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HvPortal.Gateway.Crypto;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.VmBackup;

/// <summary>
/// Notifications (ADR-033): e-mail through an SMTP server and HTTPS webhooks (generic JSON, Microsoft Teams, Slack).
/// Configured in Settings > Notifications, off until configured; nothing is sent anywhere else. The SMTP password is
/// sealed with the key ring and never returned; webhook addresses (which carry their secret in the path) are sealed too
/// and shown back masked. Webhooks are never sent to loopback, link-local or unspecified addresses, nor redirected.
/// Sending never blocks a backup: failures are logged and audited.
/// </summary>
public sealed class NotificationService(IServiceScopeFactory scopes, KeyRing keys, IHttpClientFactory httpFactory, ILogger<NotificationService> logger)
{
    public const string SettingKey = "notifications";
    private const string PasswordPurpose = "notification-smtp-password";
    private const string UrlPurpose = "notification-webhook-url";
    private const string SealedPrefix = "sealed:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed record EmailSettings
    {
        public bool Enabled { get; init; }
        public string? Host { get; init; }
        public int Port { get; init; } = 587;
        /// <summary>starttls | none (SmtpClient supports STARTTLS, not implicit TLS on port 465).</summary>
        public string Security { get; init; } = "starttls";
        public string? UserName { get; init; }
        /// <summary>Sealed SMTP password (base64), never returned by the API.</summary>
        public string? PasswordSealed { get; init; }
        public string? From { get; init; }
        public IReadOnlyList<string> To { get; init; } = [];
    }

    /// <summary>Url: the address as typed (requests) or "sealed:..." (stored); Mask: what is shown back.</summary>
    public sealed record Webhook(string Name, string Url, string Format, string? Mask = null)
    {
        [JsonIgnore]
        public bool IsSealed => Url.StartsWith(SealedPrefix, StringComparison.Ordinal);
        [JsonIgnore]
        public string Display => Mask ?? (IsSealed ? "?" : NotificationEndpoints.MaskUrl(Url));
    }

    public sealed record Settings
    {
        public EmailSettings Email { get; init; } = new();
        public IReadOnlyList<Webhook> Webhooks { get; init; } = [];
        public bool RestoreTestFailed { get; init; } = true;
        public bool AgentsOutdated { get; init; } = true;
    }

    /// <summary>severity: ok | warning | critical.</summary>
    public sealed record Message(string Event, string Severity, string Title, string Text, object? Data = null);

    public static readonly string[] WebhookFormats = ["generic", "teams", "slack"];

    public async Task<Settings> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        return await LoadAsync(db, ct);
    }

    public static async Task<Settings> LoadAsync(PortalDbContext db, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null)
        {
            return new Settings();
        }
        try
        {
            return JsonSerializer.Deserialize<Settings>(row.Value, Json) ?? new Settings();
        }
        catch (JsonException)
        {
            return new Settings();
        }
    }

    public static async Task SaveAsync(PortalDbContext db, Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(settings, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = SettingKey, Value = json, UpdatedAt = now });
        }
        else
        {
            row.Value = json;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public string SealPassword(string password) => Convert.ToBase64String(keys.Encrypt(Encoding.UTF8.GetBytes(password), PasswordPurpose));

    public Webhook SealWebhook(Webhook w) => w with
    {
        Url = SealedPrefix + Convert.ToBase64String(keys.Encrypt(Encoding.UTF8.GetBytes(w.Url), UrlPurpose)),
        Mask = NotificationEndpoints.MaskUrl(w.Url),
    };

    private string? UnsealUrl(Webhook w)
    {
        if (!w.IsSealed)
        {
            return w.Url; // written by a build before the addresses were sealed
        }
        try
        {
            return Encoding.UTF8.GetString(keys.Decrypt(Convert.FromBase64String(w.Url[SealedPrefix.Length..]), UrlPurpose));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Handler of the "notifications" HTTP client: no redirects, and the connection is refused when the name resolves
    /// only to loopback, link-local (cloud metadata), unspecified or multicast addresses.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct)).Where(a => !IsBlockedAddress(a)).ToArray();
            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves only to local or link-local addresses: not used for webhooks.");
            }
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    internal static bool IsBlockedAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6)
        {
            a = a.MapToIPv4();
        }
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal || a.IsIPv6Multicast)
        {
            return true;
        }
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork && (b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224);
    }

    private string? UnsealPassword(string? sealedValue)
    {
        if (sealedValue is null)
        {
            return null;
        }
        try
        {
            return Encoding.UTF8.GetString(keys.Decrypt(Convert.FromBase64String(sealedValue), PasswordPurpose));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            logger.LogWarning("The SMTP password cannot be decrypted: enter it again in Settings > Notifications.");
            return null;
        }
    }

    /// <summary>Sends to every configured channel. Returns the errors (empty = all sent; no channel = nothing sent, no error).</summary>
    public async Task<IReadOnlyList<string>> SendAsync(Message message, CancellationToken ct, Settings? settings = null)
    {
        settings ??= await LoadAsync(ct);
        var errors = new List<string>();
        if (settings.Email is { Enabled: true, Host: { Length: > 0 }, From: { Length: > 0 } } email && email.To.Count > 0)
        {
            try
            {
                await SendEmailAsync(email, message, ct);
            }
            catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or IOException)
            {
                errors.Add("e-mail: " + ex.Message);
            }
        }
        foreach (var hook in settings.Webhooks)
        {
            try
            {
                await SendWebhookAsync(hook, message, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                errors.Add($"webhook {hook.Name}: {ex.Message}");
            }
        }
        foreach (var e in errors)
        {
            logger.LogWarning("Notification '{Event}' not delivered: {Error}", message.Event, e);
        }
        return errors;
    }

    private async Task SendEmailAsync(EmailSettings email, Message message, CancellationToken ct)
    {
#pragma warning disable SYSLIB0014 // SmtpClient: the BCL client, STARTTLS only (documented in the settings page).
        using var client = new SmtpClient(email.Host!, email.Port)
        {
            EnableSsl = email.Security == "starttls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30000,
        };
#pragma warning restore SYSLIB0014
        if (email.UserName is { Length: > 0 } user && UnsealPassword(email.PasswordSealed) is { } password)
        {
            client.Credentials = new NetworkCredential(user, password);
        }
        using var mail = new MailMessage { From = new MailAddress(email.From!), Subject = "[HvPortal] " + message.Title, Body = message.Text, IsBodyHtml = false };
        foreach (var to in email.To)
        {
            mail.To.Add(new MailAddress(to));
        }
        await client.SendMailAsync(mail, ct);
    }

    private async Task SendWebhookAsync(Webhook hook, Message message, CancellationToken ct)
    {
        if (UnsealUrl(hook) is not { } address)
        {
            throw new InvalidOperationException("the stored address cannot be decrypted: enter it again in Settings > Notifications.");
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("only https:// webhook addresses are used.");
        }
        object body = hook.Format switch
        {
            // Teams (incoming webhook / workflow) and Slack accept a plain "text" field.
            "teams" or "slack" => new { text = $"**{message.Title}**\n\n{message.Text}" },
            _ => new { source = "HvPortal", @event = message.Event, severity = message.Severity, title = message.Title, text = message.Text, data = message.Data, timeUtc = DateTimeOffset.UtcNow },
        };
        using var http = httpFactory.CreateClient("notifications");
        http.Timeout = TimeSpan.FromSeconds(20);
        using var response = await http.PostAsJsonAsync(uri, body, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>Validation of the settings typed in the portal; null when valid.</summary>
    public static string? Validate(Settings s)
    {
        var e = s.Email;
        if (e.Enabled)
        {
            if (string.IsNullOrWhiteSpace(e.Host) || e.Host.Length > 253 || !Contracts.HyperV.RemoteHosts.IsValidComputerName(e.Host)) return "invalid_smtp_host";
            if (e.Port is < 1 or > 65535) return "invalid_smtp_port";
            if (e.Security is not ("starttls" or "none")) return "invalid_smtp_security";
            if (!IsMail(e.From) || e.To.Count is 0 or > 20 || e.To.Any(t => !IsMail(t))) return "invalid_mail_address";
            if (e.UserName is { Length: > 256 }) return "invalid_smtp_user";
            // A password is never sent over a connection without TLS.
            if (e.UserName is { Length: > 0 } && e.Security == "none") return "smtp_credentials_need_tls";
        }
        if (s.Webhooks.Count > 10) return "too_many_webhooks";
        foreach (var w in s.Webhooks)
        {
            if (string.IsNullOrWhiteSpace(w.Name) || w.Name.Length > 64) return "invalid_webhook_name";
            if (!w.IsSealed && ValidateWebhookUrl(w.Url) is { } invalidUrl) return invalidUrl;
            if (!WebhookFormats.Contains(w.Format)) return "invalid_webhook_format";
        }
        return null;
    }

    /// <summary>An address typed in the portal: https, no user info, not a loopback/link-local literal (names are checked when connecting).</summary>
    public static string? ValidateWebhookUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || url.Length > 1000 || !string.IsNullOrEmpty(u.UserInfo))
        {
            return "invalid_webhook_url";
        }
        if (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IsBlockedAddress(ip)))
        {
            return "invalid_webhook_url";
        }
        return null;
    }

    private static bool IsMail(string? s)
    {
        if (s is not { Length: >= 3 and <= 254 } || s.Any(char.IsWhiteSpace))
        {
            return false;
        }
        try
        {
            return new MailAddress(s).Address == s;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
