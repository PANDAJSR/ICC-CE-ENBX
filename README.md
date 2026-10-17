# ICC-CE ENBX presentation plugin

The plugin reuses the host's existing `Tools -> Open` button. It replaces only the host open handler and opens an ENBX file picker; no additional ENBX menu item or toolbar button is added.

After an ENBX file is selected, WebEasiNote renders it inside WebView2. The plugin uses `IPresentationSourceService` to reuse ICC-CE's PPT navigation UI and full-screen presentation lifecycle.

## Why the web content lives in a separate window

ICC-CE's main window is a layered transparent window (`AllowsTransparency = true`) because it is designed to annotate on top of PowerPoint. Inside such a window neither WebView2's `HwndHost` nor its `D3DImage`-based composition control can be combined with other WPF controls, so the host's PPT navigation bars would be hidden behind the web content.

The plugin therefore hosts WebView2 in a borderless window placed *below* the ICC-CE window, and switches the ICC-CE window to the same transparent annotation state the host uses when annotating PowerPoint. Web content shows through the transparent canvas while the ICC-CE navigation bars stay on top and remain clickable.

## Logging

Runtime diagnostics are written to the plugin log directory:

`<ICC-CE>/PluginLogs/com.pandajsr.icc-ce-enbx/`

Frontend `console.log/info/warn/error/debug`, uncaught exceptions, unhandled Promise rejections, and ENBX load errors are forwarded into this same file with a `[Frontend]` prefix. Missing embedded assets are also recorded.

### Frontend DevTools

DevTools is disabled by default. To enable it for diagnosis, set the user environment variable `ICC_CE_ENBX_DEVTOOLS=1`, restart ICC-CE, open an ENBX document, then press `F12` or `Ctrl+Shift+I` inside the WebView. The browser console opens in a separate WebView2 DevTools window. Remove the variable and restart ICC-CE after diagnosis.

Key entries to look for:

| Log line | Meaning |
|----------|---------|
| `Opening ENBX: ...` | A file was selected and handed to the plugin |
| `Host canvas switched to transparent annotation mode ...` | The transparent overlay state was applied |
| `ENBX output window shown behind the ICC-CE window` | The web content window is up |
| `[WebView] Initializing WebView2` / `WebView2 navigated to ...` | WebView2 startup |
| `Received web message: enbx:web-ready` | The page loaded and its script is running |
| `enbx:document-loaded ... pages=N` | The ENBX was parsed; `N` should be > 0 |
| `ENBX presentation active; host PPT controls should now be visible` | Host entered presentation mode |
| `Host navigation requested: ...` / `enbx:navigation-complete` | Host navigation bar drove a page change |
| `Host ended presentation source ...` / `Ending ENBX presentation` | Presentation ended, host state restored |

## Development

### 1. 配置宿主路径（.env）

在项目根目录的 `.env` 中指定 ICC-CE 本体仓库路径（支持相对路径或绝对路径）：

```env
# ICC-CE 本体仓库路径
ICC_CE_PATH=../iccce

# WebEasiNote 前端仓库路径（可选，默认 ../WebEasiNote）
WEB_EASINOTE_PATH=../WebEasiNote

# 编译配置（可选：Debug 或 Release，默认 Debug）
BUILD_CONFIGURATION=Debug

# 启用 WebView2 前端 DevTools（按 F12 或 Ctrl+Shift+I 唤起，默认 1）
ICC_CE_ENBX_DEVTOOLS=1

# 宿主启动参数（默认 --show 展开浮动工具栏；可选 --board 直接进入全屏白板）
HOST_ARGS=--show

# 启动时自动打开的测试课件（可选，支持相对路径或绝对路径）
# ENBX_FILE=./test.enbx
```

### 2. 一键编译并带插件启动宿主

```bash
pnpm dev
```

该命令会自动：
1. 编译 WebEasiNote 前端并生成最新的静态资源（`dist/`）；
2. 编译插件项目 `ICC.CE.ENBX.csproj`；
3. 编译 ICC-CE 本体项目 `InkCanvasForClass.csproj`；
4. 将编译好的插件产物自动同步至本体的 `Plugins/com.pandajsr.icc-ce-enbx/` 目录；
5. 自动将插件添加到宿主信任列表，避免权限确认弹窗阻塞 UI 消息泵；
6. 携带 `--show` 启动 ICC-CE 本体，展开桌面浮动工具栏，并加载该插件。可在工具栏点击【工具】->【打开】选择并播放 ENBX 课件。

### 3. 仅编译与部署（不启动）

```bash
pnpm build
```

## Build Plugin Package (.icpx)

```powershell
.\build-plugin.ps1
# 或使用:
pnpm build:plugin
```

The generated package is `icpx/com.pandajsr.icc-ce-enbx.icpx`. The current host must be restarted before it scans the installed plugin directory. WebView2 Runtime is required.

WebEasiNote is built first and its `dist` files are embedded into `ICC.CE.ENBX.dll` as resources. After changing WebEasiNote, run `build-plugin.ps1` again; do not copy only the old `.icpx` package.

## Compatibility note

ICC-CE does not currently expose the Tools menu as a public plugin SDK extension point. `ToolsMenuBridge` is isolated compatibility code that locates the existing `ToolsPopupContent.OpenBtn` and swaps its `ButtonMouseUp` handler. It should be replaced with an official SDK menu API if one is added later.
