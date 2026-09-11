using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ICC.CE.ENBX;

public sealed class EnbxWebViewLayer : Grid, IDisposable
{
    private const string AppOrigin = "https://enbx.local";
    private const string AppPath = "/WebEasiNote";
    private const string DocumentPrefix = "/document/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // 本控件承载在独立的普通窗口（EnbxPresentationWindow）中，
    // 不放在 ICC-CE 的分层透明主窗口里，因此可以直接使用标准 HwndHost 版本。
    private readonly WebView2 _webView = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private string? _documentPath;
    private string? _documentToken;
    private bool _isReady;
    private bool _disposed;

    public static bool DiagnosticsEnabled
        => string.Equals(Environment.GetEnvironmentVariable("ICC_CE_ENBX_DEVTOOLS"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("ICC_CE_ENBX_DEVTOOLS"), "true", StringComparison.OrdinalIgnoreCase);

    public event Action<EnbxWebMessage>? MessageReceived;
    public event Action<string>? LoadFailed;
    public event Action<string>? Diagnostic;

    public EnbxWebViewLayer()
    {
        Background = System.Windows.Media.Brushes.Black;
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;
        Children.Add(_webView);
    }

    public void OpenDocument(string path)
    {
        _documentPath = Path.GetFullPath(path);
        _documentToken = Guid.NewGuid().ToString("N");
        SetViewerVisible(true);
        _ = InitializeAndPostAsync();
    }

    public void Navigate(string direction, string requestId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
            Post(new { type = "enbx:navigate", direction, requestId })));
    }

    public void SetViewerVisible(bool visible)
        => Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 初始化 WebView2 并投递当前文档。WebView2 只有在可见并完成布局后才能创建底层控件，
    /// 因此这里先把图层显示出来，再初始化；整个过程带超时，避免无限挂起。
    /// </summary>
    private async Task InitializeAndPostAsync()
    {
        if (_disposed) return;
        if (_isReady)
        {
            PostOpenDocument();
            return;
        }

        await _initGate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (_isReady)
            {
                PostOpenDocument();
                return;
            }

            Diagnostic?.Invoke("Initializing WebView2.");
            Visibility = Visibility.Visible;
            UpdateLayout();
            await EnsureCoreWebView2WithTimeoutAsync();

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = DiagnosticsEnabled;
            core.Settings.IsStatusBarEnabled = false;
            core.AddWebResourceRequestedFilter(AppOrigin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildDiagnosticsScript());
            core.Navigate(AppOrigin + AppPath + "/index.html");
            _isReady = true;
            Diagnostic?.Invoke(
                "WebView2 navigated to " + AppOrigin + AppPath + "/index.html" +
                $"; devtoolsEnabled={DiagnosticsEnabled}");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("WebView2 initialization failed: " + ex);
            LoadFailed?.Invoke(ex.Message);
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>带超时的 WebView2 初始化，失败时换用独立用户数据目录再试一次。</summary>
    private async Task EnsureCoreWebView2WithTimeoutAsync()
    {
        try
        {
            await RunEnsureAsync(GetUserDataFolder(shared: true));
        }
        catch (Exception ex) when (
            ex is TimeoutException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            Diagnostic?.Invoke("WebView2 primary initialization failed, retrying with a private profile: " + ex.Message);
            await RunEnsureAsync(GetUserDataFolder(shared: false));
        }
    }

    private async Task RunEnsureAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        var initTask = _webView.EnsureCoreWebView2Async(environment);
        var completed = await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(30)));
        if (completed != initTask)
            throw new TimeoutException("WebView2 initialization timed out.");
        await initTask;
    }

    private static string GetUserDataFolder(bool shared)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICC-CE-ENBX",
            "WebView2");
        return shared ? root : Path.Combine(root, "instance-" + Environment.ProcessId);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = JsonSerializer.Deserialize<EnbxWebMessage>(e.WebMessageAsJson, JsonOptions);
            if (message == null) return;
            Diagnostic?.Invoke("Received web message: " + message.Type);
            if (message.Type == "enbx:frontend-log")
            {
                Diagnostic?.Invoke(
                    $"[Frontend][{message.Level}] {message.Text}" +
                    (string.IsNullOrWhiteSpace(message.Stack) ? "" : $" | stack={message.Stack}") +
                    (string.IsNullOrWhiteSpace(message.Url) ? "" : $" | source={message.Url}:{message.Line}:{message.Column}"));
            }
            else if (message.Type == "enbx:open-devtools")
            {
                OpenDevTools();
            }
            if (message.Type == "enbx:web-ready") PostOpenDocument();
            MessageReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("Web message parse failed: " + ex);
            LoadFailed?.Invoke("Invalid web message: " + ex.Message);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Diagnostic?.Invoke(
            $"NavigationCompleted: success={e.IsSuccess}, status={e.HttpStatusCode}, webError={e.WebErrorStatus}");
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (path.StartsWith(AppPath, StringComparison.OrdinalIgnoreCase))
                path = path[AppPath.Length..];
            if (path.StartsWith(DocumentPrefix, StringComparison.Ordinal))
            {
                var token = path[DocumentPrefix.Length..].Trim('/');
                if (!string.IsNullOrEmpty(_documentToken) &&
                    string.Equals(token, _documentToken, StringComparison.Ordinal) &&
                    _documentPath != null && File.Exists(_documentPath))
                {
                    var stream = File.Open(_documentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        stream, 200, "OK",
                        "Content-Type: application/octet-stream\r\nCache-Control: no-store");
                    return;
                }
                e.Response = MakeTextResponse(404, "Not Found", "Document not found");
                Diagnostic?.Invoke("Document resource rejected: " + path);
                return;
            }

            var resource = FindWebResource(path);
            if (resource == null)
            {
                e.Response = MakeTextResponse(404, "Not Found", "Resource not found");
                Diagnostic?.Invoke("Embedded web resource not found: " + path);
                return;
            }

            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                resource, 200, "OK",
                "Content-Type: " + GetContentType(path) + "\r\nCache-Control: no-cache");
        }
        catch (Exception ex)
        {
            e.Response = MakeTextResponse(500, "Internal Server Error", ex.Message);
            Diagnostic?.Invoke("Web resource request failed: " + ex);
        }
    }

    private CoreWebView2WebResourceResponse MakeTextResponse(int status, string reason, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return _webView.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(bytes), status, reason,
            "Content-Type: text/plain; charset=utf-8");
    }

    private static Stream? FindWebResource(string path)
    {
            var normalizedPath = path.TrimStart('/');
        var normalized = normalizedPath.Replace('/', '.');
        var assembly = typeof(EnbxWebViewLayer).Assembly;
        var exactName = "ICC.CE.ENBX.Web." + normalized;
        var backslashName = "ICC.CE.ENBX.Web." + normalizedPath.Replace('/', '\\');
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name =>
                string.Equals(name, exactName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, backslashName, StringComparison.OrdinalIgnoreCase));
        return resourceName == null ? null : assembly.GetManifestResourceStream(resourceName);
    }

    private void PostOpenDocument()
    {
        if (_documentPath == null || _documentToken == null) return;
        Post(new
        {
            type = "enbx:open",
            url = AppOrigin + DocumentPrefix + _documentToken,
            fileName = Path.GetFileName(_documentPath)
        });
    }

    public void OpenDevTools()
    {
        if (!DiagnosticsEnabled || _disposed || _webView.CoreWebView2 == null)
        {
            Diagnostic?.Invoke("DevTools request ignored; diagnostics are disabled or WebView2 is not ready.");
            return;
        }

        _webView.CoreWebView2.OpenDevToolsWindow();
        Diagnostic?.Invoke("WebView2 DevTools window opened.");
    }

    private static string BuildDiagnosticsScript()
        => @"
(() => {
  if (window.__enbxDiagnosticsInstalled) return;
  window.__enbxDiagnosticsInstalled = true;
  const send = (level, args, extra = {}) => {
    try {
      if (!window.chrome?.webview?.postMessage) return;
      const text = args.map(value => {
        try {
          if (typeof value === 'string') return value;
          return JSON.stringify(value);
        } catch (_) { return String(value); }
      }).join(' ');
      window.chrome.webview.postMessage({
        type: 'enbx:frontend-log',
        level,
        text,
        url: location.href,
        ...extra
      });
    } catch (_) {}
  };
  for (const level of ['log', 'info', 'warn', 'error', 'debug']) {
    const original = console[level];
    console[level] = (...args) => {
      send(level, args);
      original?.apply(console, args);
    };
  }
  window.addEventListener('error', event => send('uncaught-error', [event.message], {
    url: event.filename || location.href,
    line: event.lineno || 0,
    column: event.colno || 0,
    stack: event.error?.stack || ''
  }));
  window.addEventListener('unhandledrejection', event => send('unhandled-rejection', [event.reason?.stack || event.reason || 'Unknown rejection']));
  document.addEventListener('keydown', event => {
    if (event.key === 'F12' || (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === 'i')) {
      event.preventDefault();
      window.chrome?.webview?.postMessage({ type: 'enbx:open-devtools' });
    }
  }, true);
})();";

    private void Post(object message)
    {
        if (!_isReady || _disposed) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private static string GetContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_webView.CoreWebView2 != null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _webView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
            _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
        _webView.Dispose();
    }
}
