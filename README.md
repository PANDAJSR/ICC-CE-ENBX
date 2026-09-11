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

## Build

```powershell
.\build-plugin.ps1
```

The generated package is `icpx/com.pandajsr.icc-ce-enbx.icpx`. The current host must be restarted before it scans the installed plugin directory. WebView2 Runtime is required.

WebEasiNote is built first and its `dist` files are embedded into `ICC.CE.ENBX.dll` as resources. After changing WebEasiNote, run `build-plugin.ps1` again; do not copy only the old `.icpx` package.

## Compatibility note

ICC-CE does not currently expose the Tools menu as a public plugin SDK extension point. `ToolsMenuBridge` is isolated compatibility code that locates the existing `ToolsPopupContent.OpenBtn` and swaps its `ButtonMouseUp` handler. It should be replaced with an official SDK menu API if one is added later.
