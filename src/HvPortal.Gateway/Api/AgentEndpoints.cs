using HvPortal.Contracts.Agent;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Pki;

namespace HvPortal.Gateway.Api;

/// <summary>Endpoints served on the agent port only. Not cookie-authenticated; never reachable from the UI port.</summary>
public static class AgentEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/agent/v1").AllowAnonymous().ExcludeFromDescription();

        // Public: the CA certificate. Agents verify it against the fingerprint the administrator copied from the portal.
        g.MapGet("/ca", (InternalCertificateAuthority ca) => Results.Text(ca.CaCertificate.ExportCertificatePem(), "application/x-pem-file"));

        g.MapPost("/enroll", async (EnrollmentRequest req, HttpContext http, EnrollmentService enrollment) =>
        {
            try
            {
                return Results.Ok(await enrollment.EnrollAsync(req, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted));
            }
            catch (EnrollmentRejectedException)
            {
                return ApiSupport.Error(StatusCodes.Status401Unauthorized, "enrollment_rejected", "Invalid, expired or already used token, or invalid CSR.");
            }
        }).RequireRateLimiting("enroll");

        g.Map("/channel", (HttpContext http, AgentConnectionManager manager) => manager.HandleChannelAsync(http));
    }
}
