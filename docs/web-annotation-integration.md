# CE 放映批注接入

播放 ENBX 时由 WebEasiNote 采集、绘制、擦除及管理墨迹。CE 保留浮动工具栏与翻页条，将工具意图交给插件。

需要同时使用本次构建的 CE 主程序、PluginSdk 和 ENBX 插件。只更新 Web 资源或插件无法让旧版 CE 关闭自己的输入管线。新增的 SDK 成员尚未包含在公开的 1.8 SDK 包中，当前用相邻 `../iccce` 的本地 SDK 构建。

## 链路

1. 插件通过演示源描述的 `SupportsExternalAnnotation` 声明接管批注，提供 `AnnotationStateChanged` 和 `AnnotationCommand` 回调。
2. CE 关闭原生橡皮、WPF 和 WinRT 墨迹采集，隐藏上层墨迹画布，并让课件区域穿透到 WebView2 窗口。工具栏和翻页条继续命中。
3. CE 工具切换立即同步逻辑模式，按钮处理结束后重新应用输入路由；每 50ms 比较工具和画笔属性快照，只在变化时发送状态，覆盖颜色、透明度、笔宽和橡皮设置入口。
4. Web 的 `enbx:annotation-ready` 携带独立的批注会话 ID。插件保存它并重发当前 CE 工具状态；批注消息在云课件会话过滤之前单独校验。
5. 清空、撤销、重做使用唯一命令 ID。Web status 更新 CE 撤销/重做按钮，原生 TimeMachine 不覆盖外部历史状态。
6. 本模式不启动 `HostInkPageBridge`，翻页历史及墨迹由 Web 按页管理。退出时不运行原生分页桥的清屏恢复，避免清掉桌面原有墨迹。
7. 退出放映解除接管、停止状态计时器，恢复宿主画布和鼠标输入路由。进入白板时使用 CE 原生画布，返回课件后重新接管。

冻结沿用 Web 当前会话的冻结状态，禁止 Web 内容修改；它与 CE 原生桌面冻结记录分开。笔刷快照同步普通/荧光/激光类型、实际宽高与笔尖、InkStyle、速度混合与最小距离、平滑质量、荧光重叠设置和激光消退参数。Web 已移植 CE 的点集/速率/实时笔锋、默认异步五次平滑以及三层激光和消退；不再给荧光透明度额外乘固定 0.35。

当前仍需对齐 WPF FitToCurve、同步/GPU 平滑分支、跨笔画荧光去重叠、手掌擦和笔尾擦。SVG 模糊与 WPF BlurEffect 的像素结果及真实设备输入尚未做逐像素/延迟对照，不能据此宣称完全一致。ENBX 墨迹回写尚未实现。

## 验证

```powershell
pnpm --dir ../WebEasiNote build
dotnet build '../iccce/Ink Canvas/InkCanvasForClass.csproj' -c Release
dotnet build ICC.CE.ENBX.csproj -c Release
dotnet run --project tests/Annotation/Annotation.csproj -c Release -- '<ENBX fixture path>'
```

测试用实际 WebView2 加载插件内嵌的生产前端，经过插件真实消息入口，检查会话就绪、画笔颜色、墨迹生成、选择工具、清空、撤销/重做、面积擦切段、历史状态、冻结拒绝命令、矩形荧光笔、三层激光与自动消失。测试窗口结束后自动关闭。CE 浮动栏触控、窗口穿透与设备压力仍需在实际宿主上手动确认。

本次原始 64 MB 课件曾通过上述测试，后续重复加载出现 `Failed to fetch`；使用保留 Board.xml 和 Slides/ 页面 XML 的 31 KB 精简课件完成了最终验证。大文件重复加载稳定性尚待独立排查。

插件安装包位于 `icpx/com.pandajsr.icc-ce-enbx.icpx`。CE 输出位于 `../iccce/Ink Canvas/bin/Release/AnyCPU/net10.0-windows10.0.19041.0/`，运行时应使用该目录中的整套主程序和 SDK。
