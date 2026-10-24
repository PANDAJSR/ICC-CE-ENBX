using System.IO;

namespace ICC.CE.ENBX;

public sealed partial class EnbxWebViewLayer
{
    private readonly Dictionary<string, TaskCompletionSource<bool>> _imageRequests = new();

    public async Task<bool> InsertImageAsync(string path, CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        if (_disposed || !_isReady || _webView.CoreWebView2 == null) return false;
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _imageRequests.Add(requestId, completion);
        try
        {
            var data = await File.ReadAllBytesAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            if (completion.Task.IsCompleted || _disposed) return false;
            Post(new { type = "insert-image", requestId, dataUrl = "data:image/png;base64," + Convert.ToBase64String(data), name = Path.GetFileName(path) });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException) { return false; }
        finally
        {
            _imageRequests.Remove(requestId);
            if (!_disposed) Post(new { type = "cancel-image", requestId });
        }
    }

    private void CompleteImageRequest(EnbxWebMessage message)
    {
        if (_imageRequests.TryGetValue(message.RequestId, out var completion))
            completion.TrySetResult(message.Success);
    }

    internal void CancelImageRequests()
    {
        foreach (var request in _imageRequests)
        {
            if (!_disposed) Post(new { type = "cancel-image", requestId = request.Key });
            request.Value.TrySetResult(false);
        }
        _imageRequests.Clear();
    }
}
