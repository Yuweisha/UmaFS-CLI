using System.Net;

namespace UmaFs.Core;

/// <summary>
/// 全局网络设置：HTTP 代理。
/// HttpClient 的 handler 一旦创建就不能改代理，所以客户端要在设定代理之后再建；
/// 这里统一提供工厂，RemoteManifest 与 AssetDownloader 都走它。
/// </summary>
public static class Net
{
    private static IWebProxy? _proxy;
    private static readonly object _lock = new();

    /// <summary>当前代理；null 表示直连。</summary>
    public static IWebProxy? Proxy => _proxy;

    public static string? ProxyLabel => _proxy switch
    {
        null => null,
        WebProxy w => w.Address?.ToString(),
        _ => _proxy.ToString()
    };

    /// <summary>
    /// 设置 HTTP 代理。传 null / 空串 / "off" 表示关闭。
    /// 支持 "127.0.0.1:7890"（自动补 http://）与 "http://user:pass@host:port"。
    /// </summary>
    public static void SetProxy(string? url)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Equals("off", StringComparison.OrdinalIgnoreCase)
                || url.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                _proxy = null;
                return;
            }

            string s = url.Trim();
            if (!s.Contains("://")) s = "http://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
                throw new ArgumentException($"代理地址无效: {url}（示例: http://127.0.0.1:7890）");

            _proxy = new WebProxy(uri)
            {
                BypassProxyOnLocal = false,
                UseDefaultCredentials = false
            };
        }
    }

    /// <summary>没显式指定时，从 HTTP_PROXY / HTTPS_PROXY / ALL_PROXY 环境变量取。</summary>
    public static string? ProxyFromEnvironment()
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            string? v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    /// <summary>按当前代理设置创建 HttpClient。</summary>
    public static HttpClient CreateClient(int maxConnections = 32, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = maxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = _proxy is not null,
            Proxy = _proxy,
        };
        var c = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        // CDN 会按 UA 拦截，必须伪装成 Unity
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", RemoteManifest.UserAgent);
        return c;
    }
}