using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tatami.Services;

// Loopback HTTP supports root-relative assets and directory URLs from external generators.
public sealed class PreviewServer : IAsyncDisposable
{
    private WebApplication? _application;
    private string? _root;
    private HashSet<string> _generated = new(StringComparer.Ordinal);
    private string? _liveHtml;
    private const string LivePreviewPath = "/__tatami_live/index.html";
    public Uri? BaseUri { get; private set; }

    public async Task StartAsync(string output)
    {
        output = Path.GetFullPath(output);
        PathSafety.RejectLinks(output);
        var manifest = Path.Combine(output, ".tatami-manifest.json");
        PathSafety.RejectLinks(manifest);
        var entries = File.Exists(manifest)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(manifest))
            : null;
        var generated = new HashSet<string>(entries?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        foreach (var key in generated) PathSafety.OutputFile(output, key);
        if (_root == output && _application != null) { _generated = generated; return; }
        await DisposeAsync();
        _root = output;
        _generated = generated;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = output });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var types = new FileExtensionContentTypeProvider();
        app.Run(async context =>
        {
            if (BaseUri == null || context.Request.Host.Value != BaseUri.Authority
                || context.Request.Method is not ("GET" or "HEAD"))
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Path.Value == LivePreviewPath && _liveHtml is { } liveHtml)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(liveHtml);
                if (context.Request.Method != "HEAD") await context.Response.WriteAsync(liveHtml);
                return;
            }
            var path = Resolve(context.Request.Path.Value ?? "/");
            if (path == null) { context.Response.StatusCode = 404; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.ContentType = types.TryGetContentType(path, out var contentType) ? contentType : "application/octet-stream";
            context.Response.ContentLength = new FileInfo(path).Length;
            if (context.Request.Method != "HEAD") await context.Response.SendFileAsync(path);
        });
        try
        {
            await app.StartAsync();
            BaseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/");
            _application = app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private string? Resolve(string path)
    {
        if (_root == null) return null;
        var relative = path.TrimStart('/');
        if (relative == "" || relative.EndsWith('/')) relative += "index.html";
        if (!_generated.Contains(relative)) return null;
        try
        {
            var file = PathSafety.OutputFile(_root, relative);
            return File.Exists(file) ? file : null;
        }
        catch (IOException) { return null; }
    }

    public Uri GetUri(string file)
    {
        if (BaseUri == null || _root == null || !PathSafety.IsWithin(file, _root)) throw new InvalidOperationException("プレビューサーバーが未設定です。");
        var relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
        return new Uri(BaseUri, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)));
    }

    public void SetLivePreview(string html) => _liveHtml = html;

    public Uri GetLivePreviewUri()
    {
        if (BaseUri == null || _liveHtml == null) throw new InvalidOperationException("ライブプレビューが準備できていません。");
        return new Uri(BaseUri, LivePreviewPath.TrimStart('/'));
    }

    public bool CanNavigate(Uri uri) => uri.ToString() == "about:blank" ||
        (BaseUri != null && uri.Scheme == BaseUri.Scheme && uri.Authority == BaseUri.Authority
         && (Uri.UnescapeDataString(uri.AbsolutePath) == LivePreviewPath && _liveHtml != null
             || Resolve(Uri.UnescapeDataString(uri.AbsolutePath)) is { } path && Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)));

    public async ValueTask DisposeAsync()
    {
        if (_application != null) { await _application.StopAsync(); await _application.DisposeAsync(); }
        _application = null;
        BaseUri = null;
        _root = null;
        _liveHtml = null;
    }
}
