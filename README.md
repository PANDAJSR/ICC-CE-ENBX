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

## 希沃多账号

在希沃云课件窗口右上角点击头像和昵称，可直接切换已保存的账号，或进入“管理账号”。管理页支持添加账号、修改本机备注、重新登录和从此设备移除账号。退出当前账号会保留登录信息；移除账号只删除本机记录，不会删除云端课件。下载进行中需先完成或取消下载再切换账号。

每次添加账号均使用独立的 WebView2 数据目录，避免自动登录其他账号，并兼容不支持自定义隐私 Profile 控制器接口的运行时。插件会移除官方登录页的宣传区并居中表单；检测到个人中心跳转后提取真实凭证，验证云课件访问成功后保存账号并释放 WebView。取消登录会返回当前账号，或显示账号选择页。初始化失败时，诊断日志会记录失败阶段、错误码和异常堆栈。

账号信息保存为插件配置目录下的 `seewo_accounts.dat`，使用 Windows 当前用户的 DPAPI 加密，不能直接复制给其他 Windows 用户使用。旧版 `seewo_auth.json` 会自动迁移，成功写入加密文件后删除旧文件。同一希沃 UID 的重复登录会合并并保留本机备注；用户资料暂未返回时可能短暂显示为“希沃用户”。

手动联调建议：添加两个不同账号并切换；取消第三次登录；重启宿主检查当前账号；修改备注并重新登录同一账号检查合并；退出后选择保存账号；断网切换检查重试提示；下载期间检查切换限制。扫码、验证码和个人中心跳转需使用真实账号在宿主中验证。

## 希沃手机传图与遥控

登录希沃账号后，插件通过独立 x86 辅助程序连接本机希沃白板 5 的 IM 组件。手机图片在普通状态插入 CE 当前批注/白板，EN 放映时插入 Web 当前页；支持正在播放的 EN 课件前后步进。CE 插图需要支持按文件插图接口的宿主；辅助程序需要本机官方组件及 x86 .NET 10 运行时，不随插件分发官方 DLL。部署与验证范围见 [手机传图和 EN 遥控](docs/seewo-mobile-images.md)。用户已反馈能够发现电脑并传图，EN 遥控仍待完整真机验证。

插件设置中的「希沃手机 PPT 控制」可显示局域网配对二维码，通过宿主控制已联动的 PowerPoint/WPS 放映。关闭配对窗口即停止监听。协议检查已通过，最新握手修复及实际手机控制仍待端到端验证，详见 [PPT 手机遥控](docs/seewo-ppt-remote.md)。

## Development

### Repository layout

- `src/Plugin.cs`: plugin entry point.
- `src/Presentation/`: WebView2 playback, presentation window and range streams.
- `src/Host/`: host menu and ink integration.
- `src/Import/`: MHB import.
- `src/Settings/`: settings model and controls.
- `src/Seewo/`: cloud models, services and WPF UI.
- `Resources/`: WPF resources; root icons and `manifest.json` are package metadata.
- `scripts/`, `tests/`, `docs/`: development tools, verification and design notes.

Keep the project file and build entry points at the root so existing build commands continue to work. `bin/`, `obj/`, `icpx/`, local `.env` files and generated binaries are ignored. PNG icons are intentional source assets and should remain tracked. Use `git ls-files` to distinguish tracked files from local build output; `.gitignore` does not untrack files already committed.

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

### 4. 打包带宿主的测试整合包

```bash
pnpm build:bundle
# 可选：指定目标架构（默认 win-x64）
pnpm build:bundle win-x86
pnpm build:bundle win-arm64
```

在 Windows 上准备 Node.js、pnpm、.NET 10 SDK，并将 `iccce`、`ICC-CE-ENBX`、`WebEasiNote` 三个源码仓库放在同一父目录；首次构建前在 WebEasiNote 仓库执行 `pnpm install`。脚本读取 `.env` 中的仓库路径，但路径须与插件项目中引用的同级仓库一致。

脚本从当前工作区源码依次构建 WebEasiNote、插件和宿主，固定使用 Release（不受 `BUILD_CONFIGURATION` 影响），生成 `release/ICC-CE-ENBX-<架构>-<时间>.zip`。不会自动拉取远端更新。宿主以自包含形式发布，附带 .NET 10 运行时和预先信任的 ENBX 插件；使用独立暂存目录，不会启动或关闭当前宿主，也不会复制宿主日常使用目录中的账号、配置、缓存和日志。

把 ZIP 发给测试者，完整解压后运行 `启动 ICC-CE.cmd` 即可展开工具栏，再通过【工具】→【打开】测试 ENBX。测试机仍需 WebView2 Runtime。包内 `bundle-info.json` 记录三个仓库的提交及是否存在本地修改，反馈问题时可一并提供。

## Build Plugin Package (.icpx)

```powershell
.\build-plugin.ps1
# 或使用:
pnpm build:plugin
```

The generated package is `icpx/com.pandajsr.icc-ce-enbx.icpx`. The current host must be restarted before it scans the installed plugin directory. WebView2 Runtime is required.

WebEasiNote is built first and its `dist` files are embedded into `ICC.CE.ENBX.dll` as resources. After changing WebEasiNote, run `build-plugin.ps1` again; do not copy only the old `.icpx` package.

### CI 自动构建与产物下载

仓库已配置 GitHub Actions 自动化构建流水线：
- **每次 Push / PR / 手动触发 (workflow_dispatch)**：流水线会自动拉取前端 `WebEasiNote` 编译后打包为 `.icpx`，并在对应的 Action 运行记录下方提供 **Artifacts** 直接下载。
- **Release 发布**：推送版本 tag（例如 `v0.1.0`）或在 Actions 中手动触发选择发布时，会自动生成 GitHub Release 并将 `.icpx` 包及 SHA256 校验和附加至 Release Assets 中。

## Compatibility note

ICC-CE does not currently expose the Tools menu as a public plugin SDK extension point. `ToolsMenuBridge` is isolated compatibility code that locates the existing `ToolsPopupContent.OpenBtn` and swaps its `ButtonMouseUp` handler. It should be replaced with an official SDK menu API if one is added later.
