using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace ICC.CE.ENBX;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public const string PluginId = "com.pandajsr.icc-ce-enbx";

    private IPluginHost? _host;
    private IFileDialogService? _fileDialog;
    private IPresentationSourceService? _presentation;
    private IWindowService? _window;
    private EnbxWebViewLayer? _webView;
    private EnbxPresentationWindow? _presentationWindow;
    private HostInkPageBridge? _inkBridge;
    private ToolsMenuBridge? _toolsMenu;
    private readonly Dictionary<string, TaskCompletionSource<int>> _navigationRequests = new();
    private readonly object _navigationLock = new();
    private bool _presentationActive;
    private int _activePageCount;
    private int _currentPresentationPage;
    private bool _wasFullscreen;
    private int _ending;
    private FrameworkElement? _hostBackgroundCover;
    private Visibility _savedBackgroundCoverVisibility;
    private bool _savedTopMost;
    private bool _hostOverlayApplied;

    public override string Id => PluginId;
    public override string Name => "ENBX Presentation";
    public override string Version => "0.1.0";
    public override string Description => "Play ENBX courseware in ICC-CE and reuse the host PPT presentation UI.";
    public override string Author => "PANDA-JSR";

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);
        _host = host;
        _fileDialog = host.GetService<IFileDialogService>();
        _presentation = host.GetService<IPresentationSourceService>();
        _window = host.GetService<IWindowService>();

        if (_presentation == null || _fileDialog == null)
            throw new InvalidOperationException("The host does not provide the ENBX presentation services.");

        _webView = new EnbxWebViewLayer();
        _webView.MessageReceived += OnWebMessageReceived;
        _webView.LoadFailed += OnWebViewLoadFailed;
        _webView.Diagnostic += message => _host?.Log("[WebView] " + message);
        _presentationWindow = new EnbxPresentationWindow(_webView);
        _inkBridge = new HostInkPageBridge(this);

        _presentation.Ended += OnPresentationEnded;
        _toolsMenu = new ToolsMenuBridge(this);
        _toolsMenu.Start();
        _host.Log("ENBX plugin services initialized.");

        var uriService = host.GetService<IPluginUriService>();
        uriService?.RegisterHandler("open", request =>
        {
            if (!request.Query.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
                return false;
            _ = OpenFileAsync(path);
            return true;
        });

        host.Log("ENBX presentation plugin initialized.");
    }

    public override object? GetMainView() => null;

    public override void Shutdown()
    {
        try
        {
            EndPresentationAsync().GetAwaiter().GetResult();
            _toolsMenu?.Dispose();
            if (_presentation != null) _presentation.Ended -= OnPresentationEnded;
            _webView?.Dispose();
            _presentationWindow?.Dispose();
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to shut down the ENBX presentation plugin.", ex);
        }
    }

    internal async Task OpenFromMenuAsync()
        => await OpenMixedFileDialogAsync();

    internal void LogBridge(string message)
        => _host?.Log(message);

    private async Task OpenFromDialogAsync()
    {
        try
        {
            var path = _fileDialog?.OpenFile(
                "Open ENBX courseware",
                "ENBX files (*.enbx)|*.enbx|All files (*.*)|*.*");
            if (!string.IsNullOrWhiteSpace(path)) await OpenFileAsync(path);
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to open ENBX file.", ex);
        }
    }

    private async Task OpenMixedFileDialogAsync()
    {
        try
        {
            var path = _fileDialog?.OpenFile(
                "Open ICC-CE or ENBX file",
                "ENBX courseware (*.enbx)|*.enbx|Ink files (*.uink;*.zip;*.xml;*.icstk)|*.uink;*.zip;*.xml;*.icstk|All files (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(path)) return;

            if (string.Equals(Path.GetExtension(path), ".enbx", StringComparison.OrdinalIgnoreCase))
            {
                await OpenFileAsync(path);
                return;
            }

            OpenHostStrokeFile(path);
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to open ICC-CE or ENBX file.", ex);
        }
    }

    private static void OpenHostStrokeFile(string path)
    {
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow == null) throw new InvalidOperationException("ICC-CE main window is unavailable.");

        var methodName = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".uink" => "OpenUInkFile",
            ".zip" => "OpenICCZipFile",
            ".xml" => "OpenXMLStrokeFile",
            _ => "OpenSingleStrokeFile"
        };
        var method = mainWindow.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(mainWindow.GetType().FullName, methodName);
        method.Invoke(mainWindow, new object[] { path });
    }

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            _host?.Log($"ENBX file does not exist: {path}");
            return;
        }

        if (!string.Equals(Path.GetExtension(path), ".enbx", StringComparison.OrdinalIgnoreCase))
        {
            _host?.Log($"Ignoring non-ENBX file: {path}");
            return;
        }

        if (_presentationActive) await EndPresentationAsync();
        _host?.Log($"Opening ENBX: {path}");
        ShowPresentationWindow();
        _webView?.OpenDocument(path);
    }

    /// <summary>显示承载网页的独立窗口；必须先于 WebView2 初始化，否则控件无法创建。</summary>
    private void ShowPresentationWindow()
    {
        var mainWindow = GetHostMainWindow();
        if (mainWindow == null)
        {
            _host?.Log("Host main window was not found; ENBX output window cannot be shown.");
            return;
        }

        _presentationWindow?.ShowFor(mainWindow);
        _inkBridge?.Attach(mainWindow);
        SetHostOverlay(true);
        _host?.Log("ENBX output window shown behind the ICC-CE window.");
    }

    private static Window? GetHostMainWindow()
        => Application.Current?.MainWindow
            ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible);

    private async void OnWebMessageReceived(EnbxWebMessage message)
    {
        try
        {
            _host?.Log($"WebView message: {message.Type} page={message.Page} pages={message.PageCount} request={message.RequestId}");
            switch (message.Type)
            {
                case "enbx:request-open":
                    await OpenFromDialogAsync();
                    break;
                case "enbx:document-loaded":
                    await BeginPresentationAsync(message.PageCount, message.CurrentPage);
                    break;
                case "enbx:navigation-complete":
                    CompleteNavigation(message.RequestId, message.Page);
                    break;
                case "enbx:page-changed":
                    if (_presentationActive && message.Page > 0)
                    {
                        await _presentation!.UpdatePageAsync(message.Page);
                        SwitchInkPage(message.Page);
                    }
                    break;
                case "enbx:close":
                    await EndPresentationAsync();
                    break;
                case "enbx:error":
                    _host?.Log("WebEasiNote error: " + message.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to process a WebEasiNote message.", ex);
        }
    }

    private async Task BeginPresentationAsync(int pageCount, int currentPage)
    {
        if (pageCount <= 0 || _presentation == null || _webView == null) return;
        if (_presentationActive)
        {
            // 已在放映中：只同步页码，绝不再次 BeginAsync。
            // 再次 BeginAsync 会让宿主结束旧演示源并重建，从而中断放映。
            _activePageCount = pageCount;
            var page = Math.Clamp(currentPage, 1, pageCount);
            _host?.Log($"Presentation already active; syncing only. page={page}, pages={pageCount}");
            await _presentation.UpdatePageAsync(page, pageCount);
            return;
        }

        _host?.Log($"Begin presentation requested: page={currentPage}, pages={pageCount}, active={_presentationActive}");
        _wasFullscreen = _window?.IsFullscreen == true;
        _webView.SetViewerVisible(true);
        var descriptor = new PresentationSourceDescriptor
        {
            Id = PluginId,
            Name = "ENBX Presentation",
            PageCount = pageCount,
            CurrentPage = Math.Clamp(currentPage, 1, pageCount),
            AllowPageNumberClick = false,
            NavigateAsync = NavigateAsync
        };

        if (!await _presentation.BeginAsync(descriptor))
        {
            _host?.Log("Begin presentation rejected by host.");
            _webView.SetViewerVisible(false);
            return;
        }

        _presentationActive = true;
        _activePageCount = pageCount;
        _currentPresentationPage = descriptor.CurrentPage;
        _inkBridge?.BeginDocument(_currentPresentationPage);
        _ending = 0;
        _window?.SetFullscreen(true);
        _host?.Log("ENBX presentation active; host PPT controls should now be visible.");
    }

    private async Task<int> NavigateAsync(PresentationNavigation direction, CancellationToken cancellationToken)
    {
        if (!_presentationActive || _webView == null) return 0;

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_navigationLock) _navigationRequests[requestId] = completion;

        try
        {
            _host?.Log($"Host navigation requested: {direction}, request={requestId}");
            _webView.Navigate(direction == PresentationNavigation.Previous ? "previous" : "next", requestId);
            var newPage = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            if (newPage > 0) SwitchInkPage(newPage);
            return newPage;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (TimeoutException)
        {
            return 0;
        }
        finally
        {
            lock (_navigationLock) _navigationRequests.Remove(requestId);
        }
    }

    private void CompleteNavigation(string requestId, int page)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        lock (_navigationLock)
        {
            if (_navigationRequests.TryGetValue(requestId, out var completion))
                completion.TrySetResult(page);
        }
    }

    /// <summary>把宿主翻页同步到墨迹分页，保证批注留在各自页面。</summary>
    private void SwitchInkPage(int page)
    {
        if (page <= 0 || page == _currentPresentationPage) return;
        _currentPresentationPage = page;
        _inkBridge?.SwitchPage(page);
    }

    private async Task EndPresentationAsync()
    {
        if (Interlocked.Exchange(ref _ending, 1) != 0) return;

        _host?.Log("Ending ENBX presentation.");

        try
        {
            if (_presentation != null)
                await _presentation.EndAsync(PluginId);
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to leave ENBX presentation mode.", ex);
        }
        finally
        {
            _presentationActive = false;
            _activePageCount = 0;
            _currentPresentationPage = 0;
            _inkBridge?.EndDocument();
            _webView?.SetViewerVisible(false);
            _presentationWindow?.Hide();
            SetHostOverlay(false);
            _window?.SetFullscreen(_wasFullscreen);
            lock (_navigationLock)
            {
                foreach (var request in _navigationRequests.Values)
                    request.TrySetResult(0);
                _navigationRequests.Clear();
            }
            _ending = 0;
        }
    }

    private void OnPresentationEnded(string sourceId)
    {
        _host?.Log($"Host ended presentation source: {sourceId}");
        if (!string.Equals(sourceId, PluginId, StringComparison.OrdinalIgnoreCase)) return;
        _ = EndPresentationAsync();
    }

    private void OnWebViewLoadFailed(string message)
        => _host?.Log("WebEasiNote failed to load: " + message);

    /// <summary>
    /// 让 ICC-CE 主窗口保持“透明标注层”状态，使下方 ENBX 窗口的内容能透出来。
    /// 这正是 ICC-CE 自身标注 PowerPoint 时使用的状态，退出播放时按原值恢复。
    /// </summary>
    private void SetHostOverlay(bool enabled)
    {
        var mainWindow = GetHostMainWindow();
        if (mainWindow == null) return;

        var type = mainWindow.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        if (enabled)
        {
            if (_hostOverlayApplied) return;

            // 只隐藏挡住画面的不透明背景遮罩；不去改宿主的透明假背景，
            // 否则会给主窗口加出一层可命中表面，导致工具栏无法点击。
            _hostBackgroundCover = type.GetField("GridBackgroundCover", Flags)?.GetValue(mainWindow) as FrameworkElement;
            if (_hostBackgroundCover != null)
            {
                _savedBackgroundCoverVisibility = _hostBackgroundCover.Visibility;
                _hostBackgroundCover.Visibility = Visibility.Collapsed;
            }

            // 播放期间让 ICC-CE 保持置顶：网页窗口是普通（非置顶）窗口，
            // 这样宿主的工具栏与 PPT 翻页条一定不会被网页窗口遮挡。
            _savedTopMost = _window?.IsTopMost == true;
            _window?.SetTopMost(true);
            EnbxPresentationWindow.ForceTopMost(mainWindow, true);

            _hostOverlayApplied = true;
            _host?.Log(
                "Host canvas switched to transparent annotation mode for ENBX playback. " +
                $"previousCoverVisibility={_savedBackgroundCoverVisibility}, previousTopMost={_savedTopMost}");
            return;
        }

        if (!_hostOverlayApplied) return;

        if (_hostBackgroundCover != null)
            _hostBackgroundCover.Visibility = _savedBackgroundCoverVisibility;
        _window?.SetTopMost(_savedTopMost);
        EnbxPresentationWindow.ForceTopMost(mainWindow, _savedTopMost);

        _hostBackgroundCover = null;
        _hostOverlayApplied = false;
        _host?.Log("Host canvas appearance restored.");
    }
}

public sealed class EnbxWebMessage
{
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Error { get; set; } = "";
    public int PageCount { get; set; }
    public int CurrentPage { get; set; }
    public int Page { get; set; }
    public string Level { get; set; } = "";
    public string Text { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Stack { get; set; } = "";
}
