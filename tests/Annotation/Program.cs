using System.Reflection;
using System.Text.Json;
using System.Windows;
using ICC.CE.ENBX;
using Ink_Canvas.Plugins;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass an ENBX fixture path.");
        var application = new Application();
        var result = 1;
        application.Startup += async (_, _) =>
        {
            var layer = new EnbxWebViewLayer();
            var window = new Window { Content = layer, Width = 1000, Height = 700, ShowInTaskbar = false };
            try
            {
                var plugin = new Plugin();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                window.Show();
                Require(System.IO.File.Exists(args[0]), "Fixture not found: " + args[0]);
                typeof(Plugin).GetField("_webView", flags)!.SetValue(plugin, layer);
                var descriptor = new PresentationSourceDescriptor();
                typeof(Plugin).GetMethod("ConfigureWebAnnotation", flags)!.Invoke(plugin, [descriptor]);
                var statuses = new List<EnbxWebMessage>();
                string currentTool = "";
                string? loadError = null;
                layer.Diagnostic += text => { if (text.Contains("failed", StringComparison.OrdinalIgnoreCase)) Console.WriteLine(text); };
                layer.MessageReceived += message =>
                {
                    if (message.Type == "enbx:error") loadError = message.Error;
                    if (!message.Type.StartsWith("enbx:annotation-")) return;
                    // Exercise the real entry point, including its cloud-session filter.
                    typeof(Plugin).GetMethod("OnWebMessageReceived", flags)!.Invoke(plugin, [message]);
                    statuses.Add(message);
                    if (message.Type == "enbx:annotation-status")
                        currentTool = message.State?.Tool ?? "";
                };
                layer.LoadFailed += error => loadError = error;
                layer.OpenDocument(args[0]);
                await Until(() => statuses.Any(m => m.Type == "enbx:annotation-ready") || loadError != null);
                Require(loadError == null, "WebView load failed: " + loadError);
                Require(descriptor.SupportsExternalAnnotation, "External annotation capability missing");
                var web = (WebView2)typeof(EnbxWebViewLayer).GetField("_webView", flags)!.GetValue(layer)!;
                var core = web.CoreWebView2;
                async Task<string> Evaluate(string expression) => await core.ExecuteScriptAsync(expression);
                var state = new PresentationAnnotationState { Tool = "pen", Color = "#123456", Width = 7 };
                descriptor.AnnotationStateChanged(state);
                await Until(() => currentTool == "pen");
                var rectJson = await Evaluate("JSON.stringify((() => { const r = document.querySelector('[data-annotation-surface]').getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height}; })())");
                using var rect = JsonDocument.Parse(JsonSerializer.Deserialize<string>(rectJson)!);
                var r = rect.RootElement;
                double x = r.GetProperty("x").GetDouble() + r.GetProperty("width").GetDouble() * .3;
                double y = r.GetProperty("y").GetDouble() + r.GetProperty("height").GetDouble() * .3;
                async Task Mouse(string type, double px, double py, bool down)
                    => _ = await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                    {
                        type, x = px, y = py, button = "left",
                        buttons = down ? 1 : 0, clickCount = type == "mouseMoved" ? 0 : 1
                    }));
                await Mouse("mousePressed", x, y, true);
                await Mouse("mouseMoved", x + 150, y, true);
                await Mouse("mouseReleased", x + 150, y, false);
                await Until(() => descriptor.AnnotationStatus?.CanUndo == true);
                Require(await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "1", "Web did not render ink");
                Require(await Evaluate("document.querySelector('svg[data-ink-id] path').getAttribute('fill')") == "\"#123456\"", "Brush color not forwarded");
                descriptor.AnnotationCommand("clear");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "0");
                descriptor.AnnotationCommand("undo");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "1");
                Require(descriptor.AnnotationStatus?.CanRedo == true, "History status missing");
                descriptor.AnnotationCommand("redo");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "0");
                descriptor.AnnotationCommand("undo");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "1");
                state.Tool = "select";
                descriptor.AnnotationStateChanged(state);
                await Until(() => currentTool == "select");
                Require(await Evaluate("!!document.querySelector('[data-playback-selection]')") == "true", "Web selection tool missing");
                state.Tool = "eraser";
                state.EraserShape = "circle";
                state.EraserWidth = 24;
                descriptor.AnnotationStateChanged(state);
                await Until(() => currentTool == "eraser");
                await Mouse("mousePressed", x + 75, y - 40, true);
                await Mouse("mouseMoved", x + 75, y + 40, true);
                await Mouse("mouseReleased", x + 75, y + 40, false);
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "2");
                descriptor.AnnotationCommand("undo");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "1");
                state.Frozen = true;
                descriptor.AnnotationStateChanged(state);
                await Until(() => descriptor.AnnotationStatus?.Frozen == true);
                var count = statuses.Count;
                descriptor.AnnotationCommand("clear");
                await Until(() => statuses.Skip(count).Any(m => m.Type == "enbx:annotation-result"));
                Require(statuses.Last(m => m.Type == "enbx:annotation-result").Success == false, "Frozen clear was accepted");
                Require(await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "1", "Frozen ink was deleted");
                state.Frozen = false;
                state.Tool = "pen";
                state.BrushKind = "highlighter";
                state.Tip = "rectangle";
                state.Width = 10;
                state.Height = 20;
                state.Opacity = 0.5;
                descriptor.AnnotationStateChanged(state);
                await Until(() => currentTool == "pen" && descriptor.AnnotationStatus?.Frozen == false);
                await Mouse("mousePressed", x, y + 80, true);
                await Mouse("mouseMoved", x + 150, y + 80, true);
                await Mouse("mouseReleased", x + 150, y + 80, false);
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "2");
                Require(await Evaluate("document.querySelectorAll('svg[data-ink-id] path')[1].getAttribute('d').includes('a')") == "false", "Highlighter tip was circular");
                descriptor.AnnotationCommand("clear");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "0");
                state.BrushKind = "laser";
                state.Tip = "ellipse";
                state.Width = state.Height = 5;
                state.FadeEnabled = true;
                state.FadeDelay = 500;
                state.FadeSpeed = 10;
                descriptor.AnnotationStateChanged(state);
                await UntilAsync(async () => await Evaluate("document.querySelector('[data-annotation-surface]') !== null") == "true");
                // Wait for the forwarded brush snapshot, not merely the unchanged tool name.
                await Task.Delay(100);
                await Mouse("mousePressed", x, y + 120, true);
                await Mouse("mouseMoved", x + 150, y + 120, true);
                await Mouse("mouseReleased", x + 150, y + 120, false);
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('[data-laser-ink] path').length") == "3");
                Require(await Evaluate("document.querySelector('[data-laser-ink] path').getAttribute('stroke')") == "\"#123456\"", "Laser color missing");
                await UntilAsync(async () => await Evaluate("document.querySelectorAll('svg[data-ink-id]').length") == "0");
                Console.WriteLine("PASS: real WebView2 + plugin entry: ink/history/freeze, rectangular highlighter, three-layer laser and expiry");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { layer.Dispose(); window.Close(); application.Shutdown(); }
        };
        application.Run();
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Annotation smoke test timed out");
            await Task.Delay(50);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Annotation DOM check timed out");
            await Task.Delay(50);
        }
    }
}
