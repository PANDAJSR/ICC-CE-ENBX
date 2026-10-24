using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Ink_Canvas.Plugins;

namespace ICC.CE.ENBX;

public sealed partial class Plugin
{
    private async Task<bool> InsertMobileImageAsync(string path, CancellationToken ct)
    {
        // Decode before routing; a valid extension alone does not establish that this is an image.
        var png = path + ".normalized.png";
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var input = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > 40_000_000)
                    return false;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using var output = File.Create(png);
                encoder.Save(output);
            }
            ct.ThrowIfCancellationRequested();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return false;
            return await dispatcher.InvokeAsync(async () =>
            {
                ct.ThrowIfCancellationRequested();
                if (_presentationActive)
                {
                    _host?.Log("[SeewoMobile] 插图目标：Web 当前课件页。");
                    return _webView != null && await _webView.InsertImageAsync(png, ct);
                }
                _host?.Log("[SeewoMobile] 插图目标：CE 当前画布。");
                var canvas = _host?.GetService<ICanvasInkService>();
                // Optional capability keeps the plugin loadable with the published older SDK.
                var method = canvas?.GetType().GetMethod("InsertImageFromFileAsync", new[] { typeof(string) });
                if (method?.Invoke(canvas, new object[] { png }) is Task<bool> result) return await result;
                _host?.Log("[SeewoMobile] 当前宿主缺少按文件插图接口，请更新 ICC-CE。");
                return false;
            }).Task.Unwrap();
        }
        catch (OperationCanceledException) { throw; }
        catch { _host?.Log("[SeewoMobile] 手机图片解码或插入失败。"); return false; }
        finally { try { File.Delete(png); } catch { } }
    }
}
