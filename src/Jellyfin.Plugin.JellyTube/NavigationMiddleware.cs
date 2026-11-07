using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Injects only this plugin's navigation assets into the served web index, without changing installed web files.</summary>
public sealed class NavigationMiddleware(RequestDelegate next)
{
    public static bool Registered { get; internal set; }
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!HttpMethods.IsGet(context.Request.Method) || !(path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase))) { await next(context); return; }
        // The small index is transformed before transfer compression. Assets/media responses are never buffered.
        var encoding = context.Request.Headers.AcceptEncoding.ToString();
        var etag = context.Request.Headers.IfNoneMatch.ToString(); var modified = context.Request.Headers.IfModifiedSince.ToString();
        context.Request.Headers.Remove("Accept-Encoding"); context.Request.Headers.Remove("If-None-Match"); context.Request.Headers.Remove("If-Modified-Since");
        var original = context.Response.Body; await using var buffer = new MemoryStream(); context.Response.Body = buffer;
        try
        {
            await next(context);
            context.Response.Body = original;
            if (context.Response.StatusCode == 200 && context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                && string.IsNullOrEmpty(context.Response.Headers.ContentEncoding) && !context.Response.HasStarted)
            {
                var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                var transformed = WebTransformation.IndexHtml(new() { Contents = html }); var bytes = Encoding.UTF8.GetBytes(transformed);
                context.Response.Headers.Remove("ETag"); context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-cache"; context.Response.ContentLength = bytes.Length;
                await original.WriteAsync(bytes, context.RequestAborted);
            }
            else { buffer.Position = 0; await buffer.CopyToAsync(original, context.RequestAborted); }
        }
        finally
        {
            context.Response.Body = original;
            if (encoding.Length > 0) context.Request.Headers.AcceptEncoding = encoding;
            if (etag.Length > 0) context.Request.Headers.IfNoneMatch = etag;
            if (modified.Length > 0) context.Request.Headers.IfModifiedSince = modified;
        }
    }
}
public sealed class JellyTubeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<NavigationMiddleware>(); NavigationMiddleware.Registered = true; next(app);
    };
}
