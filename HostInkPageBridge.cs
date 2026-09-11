using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;

namespace ICC.CE.ENBX;

/// <summary>
/// Makes ICC-CE ink follow page changes of the external presentation source.
///
/// ICC-CE only performs per-page ink switching for a real PowerPoint show
/// (save current page, clear canvas + undo history, restore the target page,
/// lock ink to that page). External presentation sources registered through
/// <c>IPresentationSourceService</c> do not get that treatment, so annotations
/// used to stay on screen across page changes. This bridge replays the same
/// steps against the host's own members.
/// </summary>
internal sealed class HostInkPageBridge
{
    /// <summary>Page key used to keep the annotations that existed before playback started.</summary>
    private const int DesktopPage = 0;

    private readonly Plugin _plugin;
    private readonly Dictionary<int, byte[]> _pageInk = new();

    private Window? _mainWindow;
    private object? _timeMachine;
    private object? _inkManager;
    private FieldInfo? _inkCanvasField;
    private MethodInfo? _clearStrokesMethod;
    private MethodInfo? _clearHistoryMethod;
    private MethodInfo? _lockInkForSlideMethod;
    private bool _available;
    private int _currentPage;

    public HostInkPageBridge(Plugin plugin)
    {
        _plugin = plugin;
    }

    public bool IsAvailable => _available;

    public bool Attach(Window mainWindow)
    {
        if (_available && ReferenceEquals(_mainWindow, mainWindow)) return true;

        _mainWindow = mainWindow;
        var type = mainWindow.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        _inkCanvasField = type.GetField("inkCanvas", Flags);
        _clearStrokesMethod = type.GetMethod("ClearStrokes", Flags, null, new[] { typeof(bool) }, null);
        _timeMachine = type.GetField("timeMachine", Flags)?.GetValue(mainWindow);
        _clearHistoryMethod = _timeMachine?.GetType().GetMethod("ClearStrokeHistory", Flags);
        _inkManager = type.GetField("_singlePPTInkManager", Flags)?.GetValue(mainWindow);
        _lockInkForSlideMethod = _inkManager?.GetType().GetMethod("LockInkForSlide", Flags);

        _available = _inkCanvasField != null && _clearStrokesMethod != null;
        _plugin.LogBridge(_available
            ? "墨迹分页桥接已就绪：批注将按页保存并在翻页时切换。"
            : "墨迹分页桥接不可用：未找到宿主画布接口，批注不会跟随翻页。");
        return _available;
    }

    /// <summary>Starts a new document: keep existing desktop ink and switch to the first page.</summary>
    public void BeginDocument(int firstPage)
    {
        Run(() =>
        {
            _pageInk.Clear();
            _currentPage = DesktopPage;
            SwitchPageCore(firstPage);
        });
    }

    public void SwitchPage(int page) => Run(() => SwitchPageCore(page));

    /// <summary>Restores the annotations that were on the canvas before playback started.</summary>
    public void EndDocument()
    {
        Run(() =>
        {
            var canvas = GetCanvas();
            if (canvas == null) return;

            SavePage(_currentPage, canvas.Strokes);
            ClearCanvas();
            if (_pageInk.TryGetValue(DesktopPage, out var desktopStrokes))
                LoadPage(desktopStrokes);

            _currentPage = DesktopPage;
            _pageInk.Clear();
        });
    }

    private void SwitchPageCore(int page)
    {
        if (page <= 0 || page == _currentPage) return;

        var canvas = GetCanvas();
        if (canvas == null) return;

        SavePage(_currentPage, canvas.Strokes);
        ClearCanvas();
        if (_pageInk.TryGetValue(page, out var strokes))
            LoadPage(strokes);
        LockInkForPage(page);

        _currentPage = page;
        _plugin.LogBridge($"批注已切换到第 {page} 页。");
    }

    private InkCanvas? GetCanvas()
        => _inkCanvasField?.GetValue(_mainWindow) as InkCanvas;

    private void SavePage(int page, StrokeCollection? strokes)
    {
        if (page <= 0 || strokes == null) return;
        if (strokes.Count == 0)
        {
            _pageInk.Remove(page);
            return;
        }

        try
        {
            using var buffer = new MemoryStream();
            strokes.Save(buffer);
            _pageInk[page] = buffer.ToArray();
        }
        catch (Exception ex)
        {
            _plugin.LogBridge("保存本页批注失败：" + ex.Message);
        }
    }

    private void LoadPage(byte[] strokes)
    {
        var canvas = GetCanvas();
        if (canvas == null || strokes.Length == 0) return;

        try
        {
            using var buffer = new MemoryStream(strokes);
            canvas.Strokes.Add(new StrokeCollection(buffer));
        }
        catch (Exception ex)
        {
            _plugin.LogBridge("恢复本页批注失败：" + ex.Message);
        }
    }

    private void ClearCanvas()
    {
        try
        {
            _clearStrokesMethod?.Invoke(_mainWindow, new object[] { true });
            _clearHistoryMethod?.Invoke(_timeMachine, null);
        }
        catch (Exception ex)
        {
            _plugin.LogBridge("清空画布失败：" + ex.Message);
        }
    }

    private void LockInkForPage(int page)
    {
        try
        {
            _lockInkForSlideMethod?.Invoke(_inkManager, new object[] { page });
        }
        catch (Exception ex)
        {
            _plugin.LogBridge("锁定本页墨迹失败：" + ex.Message);
        }
    }

    /// <summary>All canvas mutations must happen on the UI thread.</summary>
    private void Run(Action action)
    {
        if (!_available || _mainWindow == null) return;

        var dispatcher = _mainWindow.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            Guard(action);
            return;
        }

        dispatcher.InvokeAsync(() => Guard(action));
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _plugin.LogBridge("墨迹分页操作失败：" + ex.Message);
        }
    }
}
