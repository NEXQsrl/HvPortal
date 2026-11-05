using HvPortal.Gateway.Api;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace HvPortal.Gateway.Hosting;

public static class HttpSecurity
{
    public const string NonceItem = "hvp-csp-nonce";

    /// <summary>
    /// Scripts: same origin only (no inline script, no eval). Styles: same origin plus a per-response nonce, used only
    /// by UI components that insert a small &lt;style&gt; element at runtime (scroll locking in dialogs and menus).
    /// </summary>
    public static string ContentSecurityPolicy(string nonce) =>
        "default-src 'self'; script-src 'self'; style-src 'self' 'nonce-" + nonce + "'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    public static string CspNonce(this HttpContext ctx) => ctx.Items[NonceItem] as string ?? "";

    /// <summary>Port separation, correlation id, security headers.</summary>
    public static IApplicationBuilder UseGatewayBaseline(this IApplicationBuilder app, GatewayOptions options)
    {
        return app.Use(async (ctx, next) =>
        {
            if (options.EnforcePortSeparation)
            {
                var agentPath = ctx.Request.Path.StartsWithSegments("/agent");
                var onAgentPort = ctx.Connection.LocalPort == options.AgentPort;
                if (agentPath != onAgentPort)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
            var correlation = ctx.GetCorrelationId();
            ctx.Response.Headers["X-Correlation-Id"] = correlation.ToString("D");
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            ctx.Items[NonceItem] = nonce;
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = ContentSecurityPolicy(nonce);
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                h.CacheControl = "no-store";
            }
            // The SPA shell is always rendered by the index handler (it carries the nonce): never served as a static file.
            if (ctx.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Request.Path = "/";
            }
            await next();
        });
    }

    /// <summary>
    /// Cross-site protections for the cookie-authenticated API and hub:
    ///  - Origin header (when present) must match the request origin or a configured development origin;
    ///  - state-changing /api requests require a valid antiforgery token in the X-CSRF-TOKEN header.
    /// Session cookies are additionally SameSite=Strict.
    /// </summary>
    public static IApplicationBuilder UseCrossSiteProtection(this IApplicationBuilder app, IReadOnlyCollection<string> extraOrigins)
    {
        return app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs"))
            {
                var origin = ctx.Request.Headers.Origin.ToString();
                if (origin.Length > 0)
                {
                    var self = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                    if (!string.Equals(origin, self, StringComparison.OrdinalIgnoreCase) && !extraOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                    {
                        await ApiSupport.Forbidden("origin_mismatch").ExecuteAsync(ctx);
                        return;
                    }
                }
                if (path.StartsWithSegments("/api") && !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method)))
                {
                    var af = ctx.RequestServices.GetRequiredService<IAntiforgery>();
                    try
                    {
                        await af.ValidateRequestAsync(ctx);
                    }
                    catch (AntiforgeryValidationException)
                    {
                        await ApiSupport.BadRequest("csrf_invalid", "Missing or invalid anti-forgery token. Reload the page.").ExecuteAsync(ctx);
                        return;
                    }
                }
            }
            await next();
        });
    }
}

/// <summary>Turns authorization failures into JSON problems with stable codes (no redirects).</summary>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Challenged)
        {
            await ApiSupport.Error(StatusCodes.Status401Unauthorized, "unauthenticated").ExecuteAsync(context);
            return;
        }
        if (result.Forbidden)
        {
            var restriction = context.User.FindFirst(PortalClaims.Restriction)?.Value;
            var code = restriction switch
            {
                Restrictions.PasswordChange => "password_change_required",
                Restrictions.MfaEnrollment => "mfa_enrollment_required",
                _ => "forbidden",
            };
            await ApiSupport.Forbidden(code).ExecuteAsync(context);
            return;
        }
        await _default.HandleAsync(next, context, policy, result);
    }
}
