using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Linq;

namespace ICC.CE.ENBX;

/// <summary>
/// Reuses the host's existing Tools -> Open button. The host SDK does not expose
/// this menu publicly, so the bridge targets the two known MainWindow properties.
/// </summary>
internal sealed class ToolsMenuBridge : IDisposable
{
    private readonly Plugin _plugin;
    private readonly List<OpenButtonBinding> _bindings = new();
    private readonly HashSet<string> _loggedStates = new(StringComparer.Ordinal);
    private DispatcherTimer? _timer;

    public ToolsMenuBridge(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void Start()
    {
        _plugin.LogBridge("ENBX 打开按钮桥接已启动");
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Loaded,
            (_, _) => AttachToToolsMenus(), dispatcher);
        _timer.Start();
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(AttachToToolsMenus));
    }

    private void AttachToToolsMenus()
    {
        var mainWindow = Application.Current?.MainWindow
            ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(window =>
                window.GetType().Name.Equals("MainWindow", StringComparison.Ordinal));
        if (mainWindow == null)
        {
            LogOnce("no-main-window", "ENBX 桥接未找到宿主主窗口");
            return;
        }

        foreach (var propertyName in new[] { "BoardToolsPopupContent", "MainToolsPopupContent" })
        {
            var contentProperty = mainWindow.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var contentField = mainWindow.GetType().GetField(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var content = contentProperty?.GetValue(mainWindow) ?? contentField?.GetValue(mainWindow);
            if (content == null)
            {
                LogOnce("no-content-" + propertyName, "ENBX 桥接未找到 " + propertyName);
                continue;
            }

            var openProperty = content.GetType().GetProperty(
                "OpenBtn",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (openProperty?.GetValue(content) is not FrameworkElement openButton)
            {
                LogOnce("no-open-" + propertyName, "ENBX 桥接未找到 " + propertyName + ".OpenBtn");
                continue;
            }
            if (_bindings.Any(binding => ReferenceEquals(binding.Button, openButton))) continue;

            var mouseDownField = FindInstanceField(openButton.GetType(), "ButtonMouseDown");
            var mouseUpField = FindInstanceField(openButton.GetType(), "ButtonMouseUp");
            if (mouseDownField == null || mouseUpField == null)
            {
                LogOnce("no-events-" + propertyName, "ENBX 桥接未找到打开按钮事件字段");
                continue;
            }

            var binding = new OpenButtonBinding(content, openButton, mouseDownField, mouseUpField);
            binding.MouseDownHandler = new MouseButtonEventHandler((_, _) => RedirectOpenHandler(binding));
            binding.MouseUpHandler = new MouseButtonEventHandler(async (_, _) =>
            {
                _plugin.LogBridge("ENBX open handler invoked.");
                CloseToolsPopup(content);
                await _plugin.OpenFromMenuAsync();
            });

            var currentMouseDown = mouseDownField.GetValue(openButton) as MouseButtonEventHandler;
            mouseDownField.SetValue(openButton, Delegate.Combine(currentMouseDown, binding.MouseDownHandler));
            _bindings.Add(binding);
            RedirectOpenHandler(binding);
            _plugin.LogBridge("ENBX 已接管工具菜单的打开按钮：" + DescribeHandlers(mouseUpField, openButton));
        }
    }

    private void LogOnce(string key, string message)
    {
        if (_loggedStates.Add(key)) _plugin.LogBridge(message);
    }

    private void RedirectOpenHandler(OpenButtonBinding binding)
    {
        var current = binding.MouseUpField.GetValue(binding.Button) as MouseButtonEventHandler;
        var retained = RemoveHostOpenHandlers(current);
        if (retained?.GetInvocationList().Any(handler => handler.Equals(binding.MouseUpHandler)) == true)
            return;

        binding.MouseUpField.SetValue(
            binding.Button,
            Delegate.Combine(retained, binding.MouseUpHandler));
        _plugin.LogBridge("ENBX 打开按钮处理器已重接：" + DescribeHandlers(binding.MouseUpField, binding.Button));
    }

    private static string DescribeHandlers(FieldInfo field, FrameworkElement button)
    {
        var handlers = field.GetValue(button) as MouseButtonEventHandler;
        if (handlers == null) return "(none)";
        return string.Join(", ", handlers.GetInvocationList().Select(handler => handler.Method.Name));
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null) return field;
        }
        return null;
    }

    private static MouseButtonEventHandler? RemoveHostOpenHandlers(MouseButtonEventHandler? handlers)
    {
        if (handlers == null) return null;

        MouseButtonEventHandler? retained = null;
        foreach (var handler in handlers.GetInvocationList())
        {
            if (handler.Method.Name.Contains("SymbolIconOpenStrokes_MouseUp", StringComparison.Ordinal)) continue;
            retained = (MouseButtonEventHandler?)Delegate.Combine(retained, handler);
        }
        return retained;
    }

    private static void CloseToolsPopup(object content)
    {
        var closeProperty = content.GetType().GetProperty(
            "CloseButtonControl",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (closeProperty?.GetValue(content) is Button closeButton)
            closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;

        foreach (var binding in _bindings)
        {
            var currentMouseDown = binding.MouseDownField.GetValue(binding.Button) as MouseButtonEventHandler;
            binding.MouseDownField.SetValue(
                binding.Button,
                Delegate.Remove(currentMouseDown, binding.MouseDownHandler));
        }
        _bindings.Clear();
    }

    private sealed class OpenButtonBinding
    {
        public OpenButtonBinding(object content, FrameworkElement button, FieldInfo mouseDownField, FieldInfo mouseUpField)
        {
            Content = content;
            Button = button;
            MouseDownField = mouseDownField;
            MouseUpField = mouseUpField;
        }

        public object Content { get; }
        public FrameworkElement Button { get; }
        public FieldInfo MouseDownField { get; }
        public FieldInfo MouseUpField { get; }
        public MouseButtonEventHandler MouseDownHandler { get; set; } = null!;
        public MouseButtonEventHandler MouseUpHandler { get; set; } = null!;
    }
}
