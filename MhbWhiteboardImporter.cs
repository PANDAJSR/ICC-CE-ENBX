using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace ICC.CE.ENBX;

/// <summary>
/// 将 MAXHUB 白板文件 (.mhb) 解析并原生导入至 ICC-CE 白板模式。
/// </summary>
public static class MhbWhiteboardImporter
{
    public sealed class MhbSlideData
    {
        public int PageIndex { get; set; }
        public string SlideId { get; set; } = string.Empty;
        public double Width { get; set; } = 1920;
        public double Height { get; set; } = 1080;
        public StrokeCollection Strokes { get; set; } = new();
        public List<UIElement> Elements { get; set; } = new();
        public Color? BackgroundColor { get; set; }
    }

    /// <summary>
    /// 解析 .mhb 文件并注入至宿主 MainWindow 的原生白板中。
    /// </summary>
    public static bool ImportToWhiteboard(string mhbFilePath, Window mainWindow, Action<string>? logger = null)
    {
        if (!File.Exists(mhbFilePath))
        {
            logger?.Invoke($"[MHB Importer] 文件不存在: {mhbFilePath}");
            return false;
        }

        try
        {
            logger?.Invoke($"[MHB Importer] 开始解析 MHB: {mhbFilePath}");
            var slides = ParseMhb(mhbFilePath, logger);
            if (slides.Count == 0)
            {
                logger?.Invoke("[MHB Importer] 未找到有效页面数据");
                return false;
            }

            logger?.Invoke($"[MHB Importer] 解析成功，共 {slides.Count} 页，准备注入宿主白板...");
            mainWindow.Dispatcher.Invoke(() => ApplySlidesToHost(slides, mainWindow, logger));
            return true;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[MHB Importer] 导入失败: {ex}");
            return false;
        }
    }

    private static List<MhbSlideData> ParseMhb(string mhbFilePath, Action<string>? logger)
    {
        var result = new List<MhbSlideData>();
        using var zip = ZipFile.OpenRead(mhbFilePath);

        // 1. 读取 Board.xml 获取页面顺序
        var slideIds = new List<string>();
        var boardEntry = zip.GetEntry("Board.xml");
        if (boardEntry != null)
        {
            using var stream = boardEntry.Open();
            var doc = XDocument.Load(stream);
            slideIds = doc.Descendants("Item").Select(x => x.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }

        // 2. 读取 Reference.xml 映射图片
        var refMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refEntry = zip.GetEntry("Reference.xml");
        if (refEntry != null)
        {
            using var stream = refEntry.Open();
            var doc = XDocument.Load(stream);
            foreach (var rel in doc.Descendants("Relationship"))
            {
                var id = rel.Element("Id")?.Value;
                var target = rel.Element("Target")?.Value;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                {
                    refMap[id] = target.Replace('\\', '/');
                }
            }
        }

        // 3. 准备图片提取缓存目录
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICC-CE-ENBX",
            "MhbCache",
            Guid.NewGuid().ToString("N"));

        // 4. 获取所有 SlideEntry
        var slideEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("Slides/Slide_", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e =>
            {
                var name = Path.GetFileNameWithoutExtension(e.FullName);
                return int.TryParse(name.Replace("Slide_", ""), out int idx) ? idx : 0;
            })
            .ToList();

        int pageIndex = 1;
        foreach (var entry in slideEntries)
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var slideElem = doc.Element("Slide");
            if (slideElem == null) continue;

            var slideData = new MhbSlideData
            {
                PageIndex = pageIndex,
                SlideId = slideElem.Element("Id")?.Value ?? $"slide_{pageIndex}",
                Width = ParseDouble(slideElem.Element("Width")?.Value, 1920),
                Height = ParseDouble(slideElem.Element("Height")?.Value, 1080)
            };

            // 解析背景色
            var bgElem = slideElem.Element("Background");
            var extElem = slideElem.Element("SlideExtension");
            var colorBgElem = extElem?.Element("ColorBackground")?.Element("ColorBrush");
            if (colorBgElem != null && TryParseColor(colorBgElem.Value, out var colorBg))
            {
                slideData.BackgroundColor = colorBg;
            }
            else if (bgElem?.Element("ColorBrush") != null && TryParseColor(bgElem.Element("ColorBrush")?.Value, out var colorBg2))
            {
                slideData.BackgroundColor = colorBg2;
            }

            var elementsNode = slideElem.Element("Elements");
            if (elementsNode != null)
            {
                foreach (var child in elementsNode.Elements())
                {
                    var tagName = child.Name.LocalName;
                    if (tagName == "Ink" || tagName == "MarkerInk")
                    {
                        var stroke = ParseInk(child);
                        if (stroke != null) slideData.Strokes.Add(stroke);
                    }
                    else if (tagName == "Picture")
                    {
                        var img = ParsePicture(child, zip, refMap, cacheDir);
                        if (img != null) slideData.Elements.Add(img);
                    }
                    else if (tagName == "Shape")
                    {
                        var shape = ParseShape(child);
                        if (shape != null) slideData.Elements.Add(shape);
                    }
                }
            }

            logger?.Invoke($"[MHB Importer] 第 {pageIndex} 页: 墨迹 {slideData.Strokes.Count} 条, 元素 {slideData.Elements.Count} 个");
            result.Add(slideData);
            pageIndex++;
        }

        return result;
    }

    private static Stroke? ParseInk(XElement inkElem)
    {
        string inkType = inkElem.Element("InkType")?.Value ?? "HardPenStroke";
        string colorHex = inkElem.Element("ForegroundColor")?.Value ?? "#FFFFFFFF";
        double thickness = ParseDouble(inkElem.Element("Thickness")?.Value, 3.0);
        bool fitToCurve = inkElem.Element("FitToCurve")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        if (!TryParseColor(colorHex, out var strokeColor))
        {
            strokeColor = Colors.White;
        }

        var pointsNode = inkElem.Element("Points");
        if (pointsNode == null) return null;

        var pts = new StylusPointCollection();
        foreach (var ptElem in pointsNode.Elements("StylusPoint"))
        {
            var parts = ptElem.Value.Split(',');
            if (parts.Length >= 2)
            {
                double x = ParseDouble(parts[0], 0);
                double y = ParseDouble(parts[1], 0);
                float pressure = 0.5f;
                if (parts.Length >= 3 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float p))
                {
                    pressure = Math.Clamp(p, 0f, 1f);
                }
                pts.Add(new StylusPoint(x, y, pressure));
            }
        }

        if (pts.Count == 0) return null;

        var da = new DrawingAttributes
        {
            Color = strokeColor,
            Width = Math.Max(0.5, thickness),
            Height = Math.Max(0.5, thickness),
            FitToCurve = fitToCurve,
            StylusTip = StylusTip.Ellipse,
            IgnorePressure = false
        };

        if (inkType.Contains("Marker", StringComparison.OrdinalIgnoreCase) ||
            inkElem.Name.LocalName == "MarkerInk" ||
            strokeColor.A < 255)
        {
            da.IsHighlighter = true;
        }

        return new Stroke(pts, da);
    }

    private static Image? ParsePicture(XElement picElem, ZipArchive zip, Dictionary<string, string> refMap, string cacheDir)
    {
        try
        {
            var source = picElem.Element("Source")?.Value ?? "";
            string refId = source.Replace("id://", "").Trim();
            if (string.IsNullOrEmpty(refId) || !refMap.TryGetValue(refId, out var targetPath))
                return null;

            var entry = zip.GetEntry(targetPath);
            if (entry == null) return null;

            Directory.CreateDirectory(cacheDir);
            var localPath = Path.Combine(cacheDir, refId + Path.GetExtension(targetPath));
            if (!File.Exists(localPath))
            {
                entry.ExtractToFile(localPath, overwrite: true);
            }

            double x = ParseDouble(picElem.Element("X")?.Value, 0);
            double y = ParseDouble(picElem.Element("Y")?.Value, 0);
            double width = ParseDouble(picElem.Element("Width")?.Value, 300);
            double height = ParseDouble(picElem.Element("Height")?.Value, 200);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(localPath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var img = new Image
            {
                Source = bmp,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill
            };
            InkCanvas.SetLeft(img, x);
            InkCanvas.SetTop(img, y);
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static FrameworkElement? ParseShape(XElement shapeElem)
    {
        try
        {
            var customGeom = shapeElem.Element("Geometry")?.Element("CustomGeometry");
            var pathStr = customGeom?.Element("Path")?.Value;
            if (string.IsNullOrWhiteSpace(pathStr)) return null;

            var geom = Geometry.Parse(pathStr);
            var fgStr = shapeElem.Element("Foreground")?.Element("ColorBrush")?.Value;
            var bgStr = shapeElem.Element("Background")?.Element("ColorBrush")?.Value;
            double thickness = ParseDouble(shapeElem.Element("Thickness")?.Value, 2.0);

            Brush strokeBrush = TryParseColor(fgStr, out var fg) ? new SolidColorBrush(fg) : Brushes.White;
            Brush fillBrush = TryParseColor(bgStr, out var bg) ? new SolidColorBrush(bg) : Brushes.Transparent;

            var pathShape = new System.Windows.Shapes.Path
            {
                Data = geom,
                Stroke = strokeBrush,
                Fill = fillBrush,
                StrokeThickness = thickness
            };

            double x = ParseDouble(shapeElem.Element("X")?.Value, 0);
            double y = ParseDouble(shapeElem.Element("Y")?.Value, 0);
            InkCanvas.SetLeft(pathShape, x);
            InkCanvas.SetTop(pathShape, y);
            return pathShape;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplySlidesToHost(List<MhbSlideData> slides, Window mainWindow, Action<string>? logger)
    {
        var mainType = mainWindow.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // 1. 确保进入白板模式
        var currentModeProp = mainType.GetProperty("currentMode", Flags);
        var currentModeField = mainType.GetField("currentMode", Flags);
        int currentMode = (int)(currentModeProp?.GetValue(mainWindow) ?? currentModeField?.GetValue(mainWindow) ?? 0);

        if (currentMode != 1)
        {
            logger?.Invoke("[MHB Importer] 宿主未处于白板模式，正在切换至全屏白板...");
            var enterWhiteboardMethod = mainType.GetMethod("ImageBlackboard_MouseUp", Flags);
            enterWhiteboardMethod?.Invoke(mainWindow, new object?[] { null, null });
        }

        // 2. 清空当前墨迹与时间机器
        var clearStrokesMethod = mainType.GetMethod("ClearStrokes", Flags, null, new[] { typeof(bool) }, null);
        clearStrokesMethod?.Invoke(mainWindow, new object[] { true });

        var timeMachineField = mainType.GetField("timeMachine", Flags);
        var timeMachine = timeMachineField?.GetValue(mainWindow);
        var clearHistoryMethod = timeMachine?.GetType().GetMethod("ClearStrokeHistory", Flags);
        clearHistoryMethod?.Invoke(timeMachine, null);

        var resetFreezeMethod = mainType.GetMethod("ResetInkFreezePageStates", Flags);
        resetFreezeMethod?.Invoke(mainWindow, null);

        // 3. 设置白板总页数与当前页码
        int totalPages = Math.Max(slides.Count, 1);
        var totalCountProp = mainType.GetProperty("WhiteboardTotalCount", Flags);
        totalCountProp?.SetValue(mainWindow, totalPages);

        var currentIndexProp = mainType.GetProperty("CurrentWhiteboardIndex", Flags);
        currentIndexProp?.SetValue(mainWindow, 1);

        // 4. 装载每页历史记录与墨迹集合
        var tmHistoriesField = mainType.GetField("TimeMachineHistories", Flags);
        var tmHistoriesArray = tmHistoriesField?.GetValue(mainWindow) as Array;

        var strokeCollectionsField = mainType.GetField("strokeCollections", Flags);
        var strokeCollections = strokeCollectionsField?.GetValue(mainWindow) as StrokeCollection[];

        var hostAsm = mainType.Assembly;
        var historyType = hostAsm.GetType("Ink_Canvas.Helpers.TimeMachineHistory");
        var commitTypeEnum = hostAsm.GetType("Ink_Canvas.Helpers.TimeMachineHistoryType");

        if (historyType != null && commitTypeEnum != null && tmHistoriesArray != null)
        {
            var userInputVal = Enum.Parse(commitTypeEnum, "UserInput");
            var elementInsertVal = Enum.Parse(commitTypeEnum, "ElementInsert");

            // 清空历史
            for (int i = 0; i < tmHistoriesArray.Length; i++)
            {
                tmHistoriesArray.SetValue(null, i);
            }

            for (int i = 0; i < slides.Count; i++)
            {
                int pageNumber = i + 1;
                if (pageNumber >= 101) break;

                var slide = slides[i];
                if (strokeCollections != null && pageNumber < strokeCollections.Length)
                {
                    strokeCollections[pageNumber] = slide.Strokes;
                }

                var historyList = new List<object>();
                if (slide.Strokes.Count > 0)
                {
                    var strokeHistory = Activator.CreateInstance(historyType, slide.Strokes, userInputVal, false);
                    if (strokeHistory != null) historyList.Add(strokeHistory);
                }
                foreach (var elem in slide.Elements)
                {
                    var elemHistory = Activator.CreateInstance(historyType, elem, elementInsertVal);
                    if (elemHistory != null) historyList.Add(elemHistory);
                }

                var pageHistoryArray = Array.CreateInstance(historyType, historyList.Count);
                for (int h = 0; h < historyList.Count; h++)
                {
                    pageHistoryArray.SetValue(historyList[h], h);
                }

                tmHistoriesArray.SetValue(pageHistoryArray, pageNumber);
            }
        }

        // 5. 恢复第一页墨迹
        var restoreStrokesMethod = mainType.GetMethod("RestoreStrokes", Flags, null, new[] { typeof(bool) }, null);
        restoreStrokesMethod?.Invoke(mainWindow, new object[] { false });

        // 6. 刷新白板索引页码展示
        var updateIndexMethod = mainType.GetMethod("UpdateIndexInfoDisplay", Flags);
        updateIndexMethod?.Invoke(mainWindow, null);

        // 7. 弹出通知提示
        var showNotificationMethod = mainType.GetMethod("ShowNotification", Flags, null, new[] { typeof(string) }, null);
        showNotificationMethod?.Invoke(mainWindow, new object[] { $"已导入 MAXHUB 白板，共 {totalPages} 页" });

        logger?.Invoke($"[MHB Importer] 成功导入 {totalPages} 页白板并完成切页初始化。");
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            if (hex.StartsWith("#"))
            {
                var converted = ColorConverter.ConvertFromString(hex);
                if (converted is Color c)
                {
                    color = c;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static double ParseDouble(string? val, double fallback)
    {
        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return d;
        return fallback;
    }
}
