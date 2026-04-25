using System.Net;
using System.Reflection;
using System.Text;
using Eidet.Core;

namespace Eidet.Service.Api;

/// <summary>
/// Serves the embedded Web UI bundle from <c>Eidet.Service.wwwroot.*</c> resources.
/// Handles MIME mapping, traversal sanitisation, long-cache headers for static
/// assets, and the <c>__VERSION__</c> placeholder substitution in <c>index.html</c>
/// (cache-busting hook driven by <see cref="EidetVersion.Current"/>).
/// </summary>
internal static class EmbeddedAssets
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
    };

    public static async Task ServeAsync(HttpListenerContext ctx, string filePath)
    {
        filePath = filePath.Replace('\\', '/').TrimStart('/');
        if (filePath.Contains(".."))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        var resourceName = $"Eidet.Service.wwwroot.{filePath.Replace('/', '.')}";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var ext = Path.GetExtension(filePath);
        ctx.Response.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        ctx.Response.StatusCode = 200;

        if (filePath == "index.html")
        {
            using var reader = new StreamReader(stream);
            var html = await reader.ReadToEndAsync();
            html = html.Replace("__VERSION__", EidetVersion.Current);
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            // Static assets: cache for 1 year (cache busted by ?v=version in index.html)
            if (ext is ".css" or ".js" or ".png" or ".svg")
                ctx.Response.Headers.Add("Cache-Control", "public, max-age=31536000, immutable");

            ctx.Response.ContentLength64 = stream.Length;
            await stream.CopyToAsync(ctx.Response.OutputStream);
        }

        ctx.Response.Close();
    }
}
