using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICC.CE.ENBX.Seewo.Services;
using Ink_Canvas.Plugins;
using QRCoder;

namespace ICC.CE.ENBX;

public sealed partial class Plugin
{
    private Window? _pptRemoteWindow;
    private PptRemoteServer? _pptRemoteServer;
    private PptRemoteState _pptRemoteState = new(0, 0, 0, "");
    private string? _pptRemoteDocument;

    public void OpenPptRemoteWindow()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(OpenPptRemoteWindow); return; }
        if (_pptRemoteWindow != null) { _pptRemoteWindow.Activate(); return; }
        var ppt = _host?.GetService<IPowerPointService>();
        if (ppt == null) { MessageBox.Show("当前宿主不提供 PowerPoint/WPS 控制服务。"); return; }
        var status = new TextBlock { Text = "等待手机连接（尚未收到 TCP 接入）。请使用希沃白板手机端扫码。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        void Refresh()
        {
            try
            {
                var connected = ppt.IsConnected;
                Volatile.Write(ref _pptRemoteDocument, connected ? ppt.GetPresentationPath() : null);
                Volatile.Write(ref _pptRemoteState, new PptRemoteState(
                    connected ? ppt.IsSlideshowActive ? 2 : 1 : 0,
                    ppt.CurrentSlide, ppt.TotalSlides, ppt.CurrentFileName ?? ""));
            }
            catch
            {
                Volatile.Write(ref _pptRemoteDocument, null);
                Volatile.Write(ref _pptRemoteState, new PptRemoteState(0, 0, 0, ""));
            }
        }
        try
        {
            Refresh();
            _pptRemoteServer = new PptRemoteServer(() => Volatile.Read(ref _pptRemoteState), async (id, page, ct) =>
            {
                // 将请求绑定到接收时的演示文件，UI 排队期间换文件不能翻动新文档。
                var expectedDocument = Volatile.Read(ref _pptRemoteDocument);
                return await dispatcher.InvokeAsync(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (_presentationActive || string.IsNullOrEmpty(expectedDocument) || !ppt.IsSlideshowActive || !ppt.IsConnected ||
                        expectedDocument != ppt.GetPresentationPath()) return false;
                    if (id == 1005)
                    {
                        if (page < 1 || page > ppt.TotalSlides) return false;
                        ppt.GoToSlide(page);
                    }
                    else if (id == 1003) ppt.NextSlide();
                    else ppt.PreviousSlide();
                    Refresh();
                    _host?.Log($"[SeewoPptRemote] 已提交手机导航命令 {id}，当前页 {ppt.CurrentSlide}。");
                    return true;
                });
            }, message => _host?.Log("[SeewoPptRemote] " + message));
            var server = _pptRemoteServer;
            using var qr = QRCodeGenerator.GenerateQrCode(server.QrCode, QRCodeGenerator.ECCLevel.M);
            using var png = new PngByteQRCode(qr);
            using var bytes = new MemoryStream(png.GetGraphic(8));
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "手机控制 PowerPoint / WPS", FontSize = 19, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "手机与电脑须在可互通的同一局域网。请先在 CE 中联动并开始放映。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
            panel.Children.Add(new Image { Source = bitmap, Width = 280, Height = 280, Stretch = Stretch.Uniform });
            panel.Children.Add(status);
            panel.Children.Add(new TextBlock { Text = "电脑监听地址：" + server.ListeningEndpoints,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            panel.Children.Add(new TextBlock { Text = "二维码有效期 15 分钟。关闭此窗口即停止手机控制；可最小化窗口继续使用。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            var window = new Window { Title = "希沃手机 PPT 控制", Content = panel, Width = 390, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.CanMinimize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            _pptRemoteWindow = window;
            server.StatusChanged += text => dispatcher.BeginInvoke(new Action(() => status.Text = text));
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
            window.Closed += (_, _) =>
            {
                timer.Stop(); server.Dispose(); _pptRemoteServer = null; _pptRemoteWindow = null;
                Volatile.Write(ref _pptRemoteState, new PptRemoteState(0, 0, 0, ""));
            };
            timer.Start();
            window.Show();
        }
        catch (Exception ex)
        {
            _pptRemoteServer?.Dispose(); _pptRemoteServer = null; _pptRemoteWindow = null;
            _host?.LogError("PPT 手机配对启动失败。", ex);
            MessageBox.Show("无法启动 PPT 手机配对：" + ex.Message);
        }
    }
}
