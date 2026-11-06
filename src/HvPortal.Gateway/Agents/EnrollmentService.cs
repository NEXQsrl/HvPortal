using HvPortal.Contracts.Agent;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Pki;
using HvPortal.Gateway.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Agents;

/// <summary>
/// Agent enrollment with one-time, short-lived tokens:
///  - token = 256-bit random value shown once to the administrator; only its SHA-256 is stored;
///  - default lifetime 15 minutes (max 60), bound to one pending host record;
///  - consumption is atomic (UPDATE ... WHERE ConsumedAt IS NULL), so a token can never enroll twice;
///  - any failure returns the same generic error to the caller; the reason goes to the audit log.
/// </summary>
public sealed class EnrollmentService
{
    private readonly PortalDbContext _db;
    private readonly InternalCertificateAuthority _ca;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;

    public EnrollmentService(PortalDbContext db, InternalCertificateAuthority ca, AuditLog audit, TimeProvider time, IOptions<GatewayOptions> options)
    {
        _db = db;
        _ca = ca;
        _audit = audit;
        _time = time;
        _options = options.Value;
    }

    /// <param name="hostId">A host id or a management agent id.</param>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateTokenAsync(Guid hostId, Guid createdBy, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        // Invalidate any earlier unused token for this host.
        await _db.EnrollmentTokens.Where(t => t.HostId == hostId && t.ConsumedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, now), ct);
        var token = Tokens.New();
        var expires = now.AddMinutes(Math.Clamp(_options.Security.EnrollmentTokenMinutes, 1, 60));
        _db.EnrollmentTokens.Add(new EnrollmentToken
        {
            HostId = hostId,
            TokenHash = Tokens.Hash(token),
            CreatedAt = now,
            ExpiresAt = expires,
            CreatedByUserId = createdBy,
        });
        await _db.SaveChangesAsync(ct);
        return (token, expires);
    }

    public async Task<EnrollmentResponse> EnrollAsync(EnrollmentRequest request, string? sourceIp, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        async Task<EnrollmentRejectedException> Reject(string reason, Guid? hostId)
        {
            await _audit.WriteAsync(new AuditRecord
            {
                Action = "agent.enroll",
                Outcome = AuditOutcome.Denied,
                AuthMethod = "enrollment-token",
                SourceIp = sourceIp,
                ResourceType = "host",
                ResourceId = hostId?.ToString("D"),
                Details = new { reason },
            }, ct);
            return new EnrollmentRejectedException();
        }

        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 128 || string.IsNullOrWhiteSpace(request.CsrPem))
        {
            throw await Reject("malformed request", null);
        }
        var hash = Tokens.Hash(request.Token);
        var token = await _db.EnrollmentTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            throw await Reject("unknown token", null);
        }
        if (token.ConsumedAt is not null)
        {
            throw await Reject("token already used", token.HostId);
        }
        if (token.ExpiresAt <= now)
        {
            throw await Reject("token expired", token.HostId);
        }
        var consumed = await _db.EnrollmentTokens
            .Where(t => t.Id == token.Id && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.ConsumedAt, now).SetProperty(t => t.ConsumedFromIp, sourceIp), ct);
        if (consumed != 1)
        {
            throw await Reject("token race lost", token.HostId);
        }

        // The token subject is either a host (agent on the host) or a management agent (remote hosts, ADR-022).
        var host = await _db.Hosts.FirstOrDefaultAsync(h => h.Id == token.HostId, ct);
        var management = host is null ? await _db.ManagementAgents.FirstOrDefaultAsync(a => a.Id == token.HostId, ct) : null;
        if (host is { ConnectionMode: HostConnectionMode.Remote } || (host is null && management is null))
        {
            throw await Reject(host is null ? "token subject deleted" : "remote hosts are not enrolled", token.HostId);
        }
        var subjectId = host?.Id ?? management!.Id;
        IssuedCertificate issued;
        try
        {
            issued = _ca.IssueAgentCertificate(request.CsrPem, subjectId, TimeSpan.FromDays(_options.Security.AgentCertificateDays));
        }
        catch (CertificateRequestRejectedException ex)
        {
            throw await Reject("csr rejected: " + ex.Message, subjectId);
        }

        // Re-enrollment replaces the identity: every previous certificate of this subject is revoked.
        await _db.AgentCertificates.Where(c => c.HostId == subjectId && c.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.RevokedAt, now).SetProperty(c => c.RevokedReason, "re-enrolled"), ct);
        _db.AgentCertificates.Add(new AgentCertificate
        {
            HostId = subjectId,
            ThumbprintSha256 = issued.ThumbprintSha256,
            SerialNumber = issued.SerialNumber,
            NotBefore = issued.NotBefore,
            NotAfter = issued.NotAfter,
            IssuedAt = now,
        });
        var machine = Sanitize(request.MachineName, 63);
        if (host is not null)
        {
            host.Status = HostStatus.Active;
            host.EnrolledAt = now;
            host.MachineName = machine;
            host.AgentVersion = Sanitize(request.AgentVersion, 32);
        }
        else
        {
            management!.Status = HostStatus.Active;
            management.EnrolledAt = now;
            management.MachineName = machine;
            management.AgentVersion = Sanitize(request.AgentVersion, 32);
        }
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditRecord
        {
            Action = "agent.enroll",
            AuthMethod = "enrollment-token",
            SourceIp = sourceIp,
            ResourceType = host is not null ? "host" : "management_agent",
            ResourceId = subjectId.ToString("D"),
            Details = new { machine, serial = issued.SerialNumber, notAfter = issued.NotAfter },
        }, ct);

        return new EnrollmentResponse
        {
            HostId = subjectId,
            CertificatePem = issued.CertificatePem,
            CaCertificatePem = _ca.CaCertificate.ExportCertificatePem(),
            Role = host is not null ? AgentRole.Host : AgentRole.Management,
        };
    }

    private static string Sanitize(string? s, int max)
    {
        var clean = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_').Take(max).ToArray());
        return clean.Length == 0 ? "unknown" : clean;
    }
}

public sealed class EnrollmentRejectedException() : Exception("Enrollment rejected.");
