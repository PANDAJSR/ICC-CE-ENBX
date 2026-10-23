# ENBX 放映元素选择实施计划

日期：2026-10-23。状态：首版已实现，真实宿主输入尚待验收。历史设计部分仅作参考，以本节和“本轮完成情况”的当前范围为准。

当前范围：复用 CE 原有“套索选”入口和高亮，Web 放映单选/套索多选/组合选择、移动/缩放/旋转、置顶/克隆开关/删除，形状颜色和图片原资源导出/百分比裁剪。明确不做墨迹选择、置底、撤销、重做。

## 目标和范围

通过 CE-ENBX 在 ICC-CE 播放本地或云 ENBX 时，使用已有“套索选”，不再额外增加“选择”按钮。点击元素或在空白区域画闭合套索后显示选框，可整体移动、缩放、旋转；菜单固定顺序为置顶、克隆开关、删除，类型专用项在前。退出放映后同一套索入口恢复原生行为。

用户明确排除本期墨迹选择。这里的操作只作用于课件元素；宿主墨迹仍按现有逻辑分页保存。课件置顶只调整课件内部顺序，不跨越宿主墨迹和工具栏。

首期采用放映会话内修改：翻页回来仍保留；关闭课件后释放；不自动覆盖 ENBX、不写回希沃云课件。将来另行接入另存为。

## 现有代码和缺口

| 层 | 现有位置 | 可复用能力 / 缺口 |
| --- | --- | --- |
| Web | `src/components/Viewer.tsx` | 已有 `play/edit`、选中元素、按 `slideId|elementId` 覆盖修改；目前覆盖表不能表达删除和重排 |
| Web | `src/components/SlideRenderer.tsx` | 已有选框与八个缩放手柄；选框尚未跟随 rotation |
| Web | `src/components/slide-renderer/useElementEditInteractions.ts` | 已有鼠标拖动和缩放；命中只测轴对齐矩形、只支持鼠标、绑定编辑模式；需要旋转坐标和 Pointer Events |
| Web | `src/components/slide-viewer/SlideViewerLayer.tsx` | 拖动、缩放、选中态只向编辑模式开放；需拆出放映交互条件 |
| Web | `src/platform/host-bridge.ts`、`src/hooks/useEnbxHostMessages.ts` | 已有 WebView2 双向消息；缺工具模式和选择状态消息 |
| Web | `src/rendering/staticPlan.ts` | 连续静态图形可能合并到位图；不能假设每个元素都有 DOM 命中目标 |
| Web | `src/enbx/slide-elements-parser.ts`、`src/types/slide.ts` | 组合已展开并保留 `groupId/groupAncestorIds`；必须定义组合选择语义，不能随意移动其中一个子元素 |
| 插件 | `src/Presentation/EnbxPresentationWindow.cs` | WebView2 独立窗口位于透明宿主窗口下，采用不激活窗口策略；需验证鼠标/触摸穿透、指针捕获、键盘路由 |
| 插件 | `src/Plugin.cs`、`src/Presentation/EnbxWebViewLayer.cs` | 已有放映生命周期与消息分发；适合接入模式桥接 |
| 宿主 | `InkCanvas.PluginSdk/IPresentationSourceService.cs` | 已有外部演示源，缺临时放映工具、输入路由和工具切换通知 |
| 宿主 | `InkCanvas.PluginSdk/IPluginHost.cs` | 支持 Floating/Whiteboard 工具栏注册，但没有随演示会话自动增删的专用契约 |
| 宿主 | `InkCanvas.PluginSdk/ICanvasToolService.cs`、`Ink Canvas/MainWindow_cs/MW_PluginCanvasExtensions.cs` | 插件独占工具当前要求 `IsWhiteboardMode`，不能直接用于外部演示源 |

宿主 `CursorIcon_Click` 在放映及“选择时显示笔迹”等设置下可能保留 InkCanvas 命中，因此仅调用鼠标工具或设置 `EditingMode=None` 不足以保证 Web 输入到达。逻辑工具状态、WPF 命中、WinRT 湿墨迹管线、窗口穿透必须一起处理。

## 官方实现依据与边界

目标：`C:\Program Files (x86)\Seewo\EasiNote5\EasiNote5_5.2.4.10148\Main`。

本轮使用 REA 静态 PE/CLI 成员与调用边分析，并使用本机 ILSpy 对特定类型做辅助反编译。没有启动官方播放器进行行为录制。反编译 C# 属于推导，不能视为原始源码或运行观察。

### 已得到的静态证据

1. `Cvte.Paint.Framework.dll` 存在独立 SelectionMode、SelectionForElementModeOperation、SelectionRotateTransformer、SelectionScaleTransformer，以及选择变换开始/结束通知。
2. `PermittedTransforms` 区分各角/边缩放、移动、旋转、固定比例等权限。因此 Web 应按元素能力显示手柄，而非所有类型统一开放全部变换。
3. `SelectionScaleTransformer` 调用元素最小尺寸、比例权限，提供旋转坐标偏移转换，区分角缩放与边缩放。ILSpy 重建还显示 Shift 比例辅助、Ctrl 中心对称缩放、Alt 关闭对齐。
4. `SelectionRotateTransformer` 静态重建显示：角度归一化到 [0,360)，保留一位小数；普通候选 0/90/180/270 度及起始角；Shift 补充每 15 度候选；Alt 取消吸附。普通吸附容差按手柄距离计算并限制到 3–15 度。特殊元素可提供自己的敏感角度。
5. `Cvte.Paint.Shell.dll` 有 SelectionOperator、GeneralSelectionBox、SelectionBoxGenerator；按类型限定后 REA 成功获取成员。
6. ILSpy 重建 `EasiNote.Business.DisplaySelectionBoxGenerator` 显示：放映与编辑选择框生成器分离；过滤锁定元素、`CanSelectInDisplayingMode=false` 和不可选择元素；单个扇形、两关键点形状有专用选框；选中非空时挂载 SelectionBar，空选择时移除附件。
7. ILSpy 重建 `BringToFrontMenuItem` 调用 `BringElementToFront`，`DeleteMenuItem` 绑定 `DeleteSelectedElements`。两者标记为 `EduBoard.V5.ElementEditMenuItem`，仅证实编辑菜单命令，不能据此认定官方放映菜单必然包含相同条目。
8. `EasiNote.UI.DisplaySelectionBoxPopup` 重建采用 BottomCenter 放置，但尚未验证其实际调用场景和运行位置。

### REA 证据索引

| 证据 | 内容 |
| --- | --- |
| `ev_ec75afca2a65a9c38b7583a7b9d7abbd92c12e999f510257fd7db71e6a907c2a` | Framework 全量静态成员母证据 |
| `ev_91a0994fa8e473d31a29a902a533e9b71b3472356359c5143b94cbc8fd11daaf` | 七个选择/变换相关类型的限定成员证据，覆盖 complete |
| `ev_00df45efac87112d3eac3438a1f00736e461e09e3737c3d8721cc6c2f5b33114` | 上述方法分页，包括旋转和缩放方法 |
| `ev_036bc42de0b3a4908675ce5486def2c0843f4e8946d05d55a23caad823b1def2` | 上述调用边分页，包括敏感角度、对齐与修饰键调用 |
| `ev_4e2ec61a7b147b1e15beadbb7ef3706c76bebb0bdc3dcd9c2ad0fe772c46a68f` | Shell 三个选框类型的限定成员母证据，覆盖 complete |
| `ev_7b5fd4f4e35102351f6606e5330ae4427dcf07e2407ad61fec86d3875cc1fb97` | Business 限定类型分析仍触及 64 MiB 元数据表示预算，覆盖 unavailable；其行为描述来自辅助 ILSpy，非该证据的完整验证 |

Framework SHA-256：`eea0a8fa596c7a68d0b07c6ef6684ffa4ed577f3572c2710e30a5c4f664cd803`，MVID：`f8c61611-1f85-4b3e-bd14-69b762f4a607`。Shell SHA-256：`481012f25286ac000ac14b15ab1a534476824e921e5b8b034f229ca159c74277`。Business SHA-256：`841912895537624c03a0b5791a3feb61667a9543f2ef15ee51af78b7c2b6f0c0`。

### 尚需定点核对

- 官方放映菜单的条目、顺序、按类型显隐、选框附件的实际位置。
- 锁定与放映可选择字段的 XML 序列化位置、默认值、类型覆盖；不能仅依据属性名字新增 XML 规则。
- 点击和拖动与媒体控件、超链接、元素触发动画的优先级。
- 组和特殊学科组件的变换限制，文字缩放是否同时改变字号。
- 边界外拖动、触摸双指变换、选框样式和手柄尺寸。

上述项目应在实现相应能力前补齐；不阻碍先完成宿主通信和普通元素的垂直联调。

## 分层设计

### 宿主：原套索入口和输入路由

为外部演示源增加通用、可选能力，具体 API 名称在实现时确定：

- 演示源提供可选选择能力；浮动栏、白板栏、液态玻璃栏和旧套索入口复用逻辑 `select` 和原生高亮，不添加会话工具栏组件。
- 当前工具状态通知，覆盖画笔、橡皮、鼠标、其他宿主工具与插件演示选择。
- 演示内容输入接管租约：课件区域透传到下层窗口，宿主工具栏/菜单/翻页条继续正常命中；墨迹保持显示但不参与选择。若底层无法只靠 WPF 命中实现，配合宿主 HWND 命中策略验证，不能把整个窗口设为穿透。
- 进入前取消在途湿墨和手势；退出、异常、插件卸载、演示结束时释放路由和租约。用户主动选择新工具时保留新工具，不被旧模式恢复覆盖。
- 按最新需求复用宿主逻辑 `select`，但物理 `EditingMode=None`，WinRT 管线解析为 Cursor，InkCanvas 不参与命中；不会激活墨迹套索。非支持源保持原生选择。

优先修改 SDK 和宿主正式实现。不要继续扩充依赖控件名称、反射和定时轮询的工具栏注入逻辑。

### 插件：模式桥接

新增 `HostPresentationSelectionBridge`，处理工具注册、宿主模式变化、Web 就绪/重载后的模式同步和资源释放。`EnbxWebViewLayer` 提供明确的模式/动作发送入口，避免散落脚本注入。

拟定消息（新增协议，非现有接口）：

```ts
// 宿主 -> Web
{ type: 'enbx:set-interaction-mode', mode: 'play' | 'select', sessionId, requestId }
// 菜单操作直接在 Web 执行，不经宿主消息。

// Web -> 宿主
{ type: 'enbx:interaction-state', mode, ready, sessionId, requestId }
{ type: 'enbx:interaction-ready' }
```

插件只在 Web 确认模式后更新最终选中态；等待期间给出忙态，超时/失败回退。过期会话、页或选择版本的动作拒绝执行。拖动逐帧留在 Web，桥接只发送模式、命令和必要的状态变更。

### Web：放映交互独立于文档编辑

保留 `viewerMode=play/edit`，额外引入 `interactionMode=play/select`。放映选择不打开 XML 侧栏、不进入文字内容编辑、不重置播放进度。

建议新增小模块：

- `viewer/selection-session.ts`：按 document/session、slide、element 管理会话修改、删除集合和稳定顺序。
- `viewer/usePlaybackSelection.ts`：会话变换、删除、重排、副本；抬起时提交手势。
- `components/selection/SelectionOverlay.tsx`：旋转选框、手柄、取消和屏幕大小固定的命中区。
- `components/selection/SelectionActionBar.tsx`：选框下方操作菜单，空间不足时翻到上方或限制在课件视口内。
- `components/selection/useSelectionPointer.ts`：Pointer Events、capture、cancel、拖动阈值和事件消费。
- `viewer/selection-geometry.ts`：统一屏幕/页面/元素局部坐标转换和变换数学。
- `viewer/element-capabilities.ts`：按类型与元素属性给出 select/move/resize/rotate/delete/reorder 和比例/最小尺寸限制。

选择入口为宿主原有“套索选”；元素操作菜单由 Web 在选框下方渲染。纯浏览器模式也能复用该菜单。Web 保留宿主工具栏安全区，避免被上层 WPF 窗口遮挡。

## 交互规则

1. 选择模式点击可见可选元素，显示选框；点击空白取消。事件在选择路由处消费，避免同一次操作触发翻页、链接或元素点击动画。
2. 命中按当前绘制顺序从上向下；先逆变换指针到元素局部空间，处理 rotation、flip、祖先和动画变换。已隐藏、退出、未出现元素不命中。线条和形状再使用几何命中策略，不能长期只依赖矩形。
3. 拖动超过阈值开始移动；拖动结束抑制随后的 click；pointercancel/失焦/换页取消预览并恢复最后提交值。
4. 缩放在局部坐标计算，固定对边/对角；所有边/角手柄强制等比例（不依赖 Shift）并限制最小尺寸。文本使用会话内容比例整体缩放字号、间距、内边距、边框和效果，不仅改变文本框；原文本数据保持不变。旋转选框跟随真实 rotation，手柄显示尺寸不随页面 scale 失控。
5. 旋转按中心及 atan2 差值计算，处理跨越 0/360；对普通类型实现已核对的官方吸附规则。特殊类型在能力确认后接入。
6. 支持普通图文单选和空白处套索多选；套索自动闭合，覆盖至少 50% 即选中。使用元素旋转局部矩形面积和 64 条扫描线近似，凹形/自交按奇偶填充；组合按成员面积加权达到阈值后整体选择，不要求完全包围所有成员。锁定、隐藏、正在动画的成员会阻止整组选择。Shift/Ctrl/Meta 可追加；选框内空白拖动整体移动。组合解组、特殊节点编辑仍不支持。
7. 选择优先命中，不让播放器内控件抢到第一次选择点击。退出选择模式恢复原有媒体/学科控件交互；如需选择中播放媒体，单独设计显式入口。
8. 选择模式禁止画布空白点击翻页和滑动翻页；宿主上一页/下一页按钮仍可用。换页前结束/取消在途操作并清空选中态，保留已提交会话修改。
9. 选中或正在变换的元素从静态合并段中拆出，保持原 Z 顺序；提交/删除/重排以新不可变页面对象使缓存失效。不能在静态位图上再叠画一份未剔除的元素。
10. 会话模型区分原始几何、已提交操作、动画变换与手势预览。动画控制可见性和播放进度；用户几何操作不能被下一帧样式直接覆盖。首期变换时冻结目标动画的合成值，提交后按明确的变换组合策略恢复，不重建整页播放器。
11. 删除为会话 tombstone，原始 ID 与 XML 保留；渲染和点击使用过滤后的元素集合。

## 交付顺序

### P0：行为基线和垂直联调

- 将官方待核对点按普通图片、文字、形状、组合、锁定对象、媒体做小样本矩阵。
- 宿主原套索入口 -> 插件消息 -> Web 单选/套索边框；再切画笔验证输入恢复。
- 优先验证带已有墨迹、鼠标/触摸/笔、不同 DPI、宿主菜单遮挡、Web 不激活窗口下的输入。
- P0 结束条件：入口与输入切换可靠；不涉及墨迹选择。

### P1：普通元素操作闭环

- 从现有编辑逻辑提取公共选择几何和指针逻辑，补旋转选框、移动、缩放、旋转。
- 会话修改、置顶/克隆/删除、翻页返回保留。
- 完成本地和云课件消息同步、加载失败/未就绪禁用、关闭和重载清理。
- 此阶段覆盖用户要求的基本能力；不自动保存原文件。

### P2：兼容性补齐

- 组合选择、特殊选框、锁定/放映选择字段、元素类型权限。
- 动画交互、媒体/学科组件、静态合并渲染和大量元素性能。
- 套索多选已按最新需求接入；双指旋转缩放、复制、另存为按独立后续需求推进。

## 验收

- 几何单元测试：旋转后命中、45/90/270 度缩放对角固定、不同页面缩放和留白、跨 0 度旋转、最小尺寸。
- 命令测试：移动/删除/重排/克隆取消、组内顺序、页与文档隔离。
- 浏览器交互：鼠标、触摸、笔；拖动不翻页、选中不触发动画；切模式/换页/pointercancel 没有残留选框和捕获。
- ICC-CE 集成：已有墨迹可见且不阻挡课件选择；工具栏/翻页/菜单能点；退出、异常、插件卸载后输入恢复。
- 真实课件：本地与云课件各至少一份；含媒体、动画、组和超过 32 个可合并形状的页面，验证缓存与层级一致。
- 构建按 Web、插件、宿主分别执行；联调部署另行执行。当前 `scripts/build.mjs` 会结束运行中的宿主并部署，不应把它当只读构建检查。

## 本轮完成情况

- 已完成：三仓实现；宿主复用原套索按钮/逻辑模式/高亮和输入切换，删除临时“选择”按钮及注册注入；插件可选 SDK 能力探测；Web 独立放映选择、套索多选、旋转几何、组合、会话重排/删除/拖出克隆；形状颜色、图片原资源导出与百分比裁剪。
- 已确认：官方菜单、克隆路径与 `IsLocked` / `CanClone` 默认 false 的直接子字段。证据见 `D:/Code/EasiNote-Resarch/topics/playback-selection/menu-findings.md` 和 `clone-findings.md`。
- 菜单外观按静态官方 `DisplayFunctionBar` 还原：64×56 按钮、24×24 原路径图标、微软雅黑 12 / 行高 20、4 外圆角、双层 `#F4F6F9` 80% 背景、原九宫格阴影、4%/8% 悬停/按下、克隆/颜色面板选中绿色 `#00C881`。类型项和固定项间 1×36 分隔线，水平边距各 4；普通项顺序和克隆开关语义不变。依据 `menu-style-findings.md`；Web 已保留资源出处和未验证范围。
- 限制：所有指针统一保留原件、移动新副本，副本不继承原动画；未复现官方触摸路径的 ID 和动画归属交换。正在动画的元素暂不变换；组合使用外接矩形与等比缩放；特殊类型专用手柄、完整权限、多指变换尚未实现。套索 50% 覆盖阈值是本实现策略，非官方运行行为结论。菜单绝对选框间距、DPI/字体栅格化/运行时资源覆盖和逐像素截图同一性未验证；描边/填充弹出面板及图片裁剪界面不声称完全复刻官方。
- 实际模式消息不带 sessionId/requestId；Viewer 挂载时请求重同步。四个 SpecialElement 能力只确认运行时属性，不推断 XML 标签。
- 待验收：真实宿主鼠标/触摸/笔、DPI、本地/云课件及输入恢复。没有执行会结束宿主的部署脚本。
- 退出放映穿透修复：此前 `DetachPresentationSelection` 只切换逻辑 cursor/EditingMode，并还原放映期命中快照，可能留下画布命中和关闭的 HWND 穿透。现在在清除放映标志后重用鼠标工具的 `ApplyCursorInputRouting`，按桌面设置重算画布显示/命中、透明背景和窗口穿透；保留已选择图片/SVG 的原有例外。不额外截图或保存墨迹。仍待真实桌面点击验收。

现有工作区存在云课件加载相关未提交修改；本轮保留这些改动。
