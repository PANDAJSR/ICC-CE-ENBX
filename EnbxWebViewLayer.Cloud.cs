using System.IO;
using ICC.CE.ENBX.Seewo.Services;
using Microsoft.Web.WebView2.Core;

namespace ICC.CE.ENBX;

public sealed partial class EnbxWebViewLayer
{
    private CloudCoursewareSession? _cloudSession;
    public string? CloudSessionId => _cloudSession?.Id;

    public void OpenCloudDocument(CloudCoursewareSession session)
    {
        CloseCloudDocument();
        _documentPath = null;
        _documentToken = null;
        _cloudSession = session;
        SetViewerVisible(true);
        _ = InitializeAndPostAsync();
    }

    public void CloseCloudDocument()
    {
        if (_cloudSession != null && !_disposed)
            Post(new { type = "enbx:close-cloud", sessionId = _cloudSession.Id });
        _cloudSession?.Dispose();
        _cloudSession = null;
    }

    public void CancelNavigation(string requestId)
        => Post(new { type = "enbx:cancel-navigation", requestId });

    private async void HandleCloudResource(CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        var session = _cloudSession;
        Stream? stream = null;
        try
        {
            var uri = new Uri(e.Request.Uri);
            var prefix = "/courseware/" + session?.Id + "/";
            if (session == null || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new FileNotFoundException();
            if (e.Request.Method is not ("GET" or "HEAD"))
            {
                e.Response = MakeTextResponse(405, "Method Not Allowed", "Method not allowed");
                return;
            }
            var relative = uri.AbsolutePath[prefix.Length..];
            if (relative == "manifest.json")
            {
                var bytes = session.Manifest();
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(e.Request.Method == "HEAD" ? [] : bytes), 200, "OK",
                    $"Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store");
                return;
            }
            if (!relative.StartsWith("files/", StringComparison.Ordinal)) throw new FileNotFoundException();
            var logicalPath = Uri.UnescapeDataString(relative[6..]);
            var priority = uri.Query.Contains("prefetch=1", StringComparison.Ordinal) ? 2 :
                logicalPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var localPath = await session.GetFileAsync(logicalPath, priority);
            if (_disposed || !ReferenceEquals(session, _cloudSession)) return;
            var length = new FileInfo(localPath).Length;
            var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
            if (!CoursewareRange.TryParse(range, length, out var start, out var count))
            {
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(null, 416,
                    "Range Not Satisfiable", $"Content-Range: bytes */{length}");
                return;
            }
            var partial = range != null;
            var headers = $"Content-Type: {GetContentType(logicalPath)}\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store";
            if (partial) headers += $"\r\nContent-Range: bytes {start}-{start + count - 1}/{length}";
            if (e.Request.Method != "HEAD") stream = new CoursewareRangeStream(localPath, start, count);
            e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(stream,
                partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
            stream = null; // WebView2 持有响应流直至读完。
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(session, _cloudSession))
            {
                e.Response = MakeTextResponse(ex is FileNotFoundException ? 404 : 503,
                    "Courseware Unavailable", "课件文件暂不可用，请重试");
                Diagnostic?.Invoke("Cloud resource failed: " + ex.GetType().Name);
            }
        }
        finally
        {
            stream?.Dispose();
            deferral.Complete();
        }
    }
}
