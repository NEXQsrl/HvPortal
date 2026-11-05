namespace HvPortal.Gateway.Hosting;

/// <summary>
/// Serves the single-page application shell (wwwroot/index.html) with the per-response CSP nonce substituted for
/// <see cref="NoncePlaceholder"/> (read by the UI to label the few runtime &lt;style&gt; elements its components create).
/// Static assets are served by the static file middleware; index.html is never served as a static file.
/// </summary>
public static class SpaShell
{
    public const string NoncePlaceholder = "__CSP_NONCE__";

    public static void MapSpaShell(this WebApplication app)
    {
        app.MapGet("/", ServeAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapFallback(ServeAsync).AllowAnonymous();
    }

    private static async Task ServeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var file = env.WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("The web UI is not installed (wwwroot/index.html missing).");
            return;
        }
        string template;
        await using (var stream = file.CreateReadStream())
        using (var reader = new StreamReader(stream))
        {
            template = await reader.ReadToEndAsync(ctx.RequestAborted);
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        await ctx.Response.WriteAsync(template.Replace(NoncePlaceholder, ctx.CspNonce(), StringComparison.Ordinal), ctx.RequestAborted);
    }
}
