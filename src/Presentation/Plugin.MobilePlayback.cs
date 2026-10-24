using System.Windows;
using ICC.CE.ENBX.Seewo.Services;
using Ink_Canvas.Plugins;

namespace ICC.CE.ENBX;

public sealed partial class Plugin
{
    private MobilePlaybackState? _mobilePlayback;
    private CancellationTokenSource? _mobilePlaybackLifetime;

    private void BeginMobilePlayback(EnbxWebMessage message)
    {
        EndMobilePlayback();
        if (!_presentationActive || message.SlideIds.Length != _activePageCount ||
            message.SlideIds.Any(string.IsNullOrWhiteSpace)) return;
        var cloud = _webView?.CloudDocument;
        var sessionId = Guid.NewGuid().ToString("N");
        _mobilePlaybackLifetime = new CancellationTokenSource();
        _mobilePlayback = new MobilePlaybackState(sessionId, cloud?.Cid ?? sessionId,
            cloud?.Name ?? message.FileName, cloud?.Version ?? 0, _currentPresentationPage, message.SlideIds);
        _seewoDeviceSession?.UpdatePlayback(_mobilePlayback);
    }

    private void UpdateMobilePlaybackPage(int page)
    {
        if (_mobilePlayback == null) return;
        _mobilePlayback = _mobilePlayback with { Page = page };
        _seewoDeviceSession?.UpdatePlayback(_mobilePlayback);
    }

    private void EndMobilePlayback()
    {
        _mobilePlaybackLifetime?.Cancel();
        _mobilePlaybackLifetime?.Dispose();
        _mobilePlaybackLifetime = null;
        _mobilePlayback = null;
        _seewoDeviceSession?.UpdatePlayback(null);
    }

    private async Task<bool> NavigateFromMobileAsync(MobilePlaybackState expected, bool next, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;
        return await dispatcher.InvokeAsync(async () =>
        {
            if (!_presentationActive || _mobilePlayback?.SessionId != expected.SessionId ||
                _mobilePlaybackLifetime == null || _presentation == null) return false;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _mobilePlaybackLifetime.Token);
            _host?.Log($"[SeewoMobile] 手机请求{(next ? "下一步" : "上一步")}。");
            var page = await NavigateAsync(next ? PresentationNavigation.Next : PresentationNavigation.Previous, lifetime.Token);
            if (lifetime.IsCancellationRequested || page <= 0 || _mobilePlayback?.SessionId != expected.SessionId) return false;
            // 手机触发导航不经过宿主的按钮回调，需显式更新所有宿主页码条。
            await _presentation.UpdatePageAsync(page, cancellationToken: lifetime.Token);
            return true;
        }).Task.Unwrap();
    }
}
