using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace NetWatch.Web.Infrastructure;

/// <summary>
/// Serves the React UI from resources embedded in NetWatch.Web.dll (built from ClientApp/dist), so the
/// single-file exe needs no files on disk. Unknown non-API paths fall back to index.html (client-side routing).
/// </summary>
public static class EmbeddedUi
{
    public static IApplicationBuilder UseEmbeddedUi(this WebApplication app)
    {
        IFileProvider provider;
        try
        {
            provider = new ManifestEmbeddedFileProvider(typeof(EmbeddedUi).Assembly, "ClientApp/dist");
        }
        catch (InvalidOperationException)
        {
            // Built without the UI (e.g. backend-only development build).
            provider = new NullFileProvider();
        }

        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = ctx =>
            {
                // Vite emits content-hashed file names under /assets, which can be cached forever.
                ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });

        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var index = provider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("The web UI was not included in this build. Run build.ps1 to build it.");
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body);
        });

        return app;
    }
}
