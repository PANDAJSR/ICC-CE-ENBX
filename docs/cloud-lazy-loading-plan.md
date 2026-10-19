# 云课件分页与资源按需加载方案

日期：2026-10-18。状态：已实现首版云播放闭环；实施及验证情况见 `cloud-lazy-loading-implementation.md`。

## 1. 目标与首屏定义

云课件获取文件清单和最小元数据后，只下载并解析目标页 XML，即可显示该页已有的文字、图形和布局。图片、背景和媒体分别加载，其他页面不阻塞首屏。后台预取与用户翻页共用一个可提升优先级的文件队列。

“一页 XML 即可显示”需要区分三个时间点：

- `page-core-ready`：最小元数据及该页 XML 已就绪，正文可以提交；缺失图片保留布局并显示加载状态。
- `page-visual-ready`：该页初始可见的图片和背景已完成加载/解码，或明确进入可重试失败状态。
- `offline-complete`：清单全部文件通过校验，可离线打开或导出完整 ENBX。

首屏关键路径仍需要文件清单、`Board.xml`（页序与总页数）、`Reference.xml`（资源映射；确实不存在时允许空映射）及目标页 XML。`Document.xml` 的名称等信息可先由详情接口提供，随后补齐；若未来识别到渲染必需的全局字段，再纳入最小集。这里不承诺只发起一次 XML 请求，也不承诺图片尚未到达时已有完整画面。

## 2. 现有阻塞与改动位置

| 层 | 当前行为 | 改造方向 |
| --- | --- | --- |
| `src/Seewo/UI/SeewoBrowserControl.xaml.cs` | 播放与下载共用 `DownloadAndPackageAsync` | 播放创建云会话；“下载到本地”继续全量下载/打包 |
| `src/Seewo/Services/SeewoDownloader.cs` | 4 路下载全部文件，再打包 ZIP，最后返回路径 | 提取可复用文件获取器；散文件缓存直接供播放器读取 |
| `Plugin.cs:448` | 输入只能是本地 ENBX 路径 | 增加 `OpenCloudCoursewareAsync`，复用放映窗口和演示源生命周期 |
| `src/Presentation/EnbxWebViewLayer.cs` | `/document/{token}` 只返回整个文件 | 增加会话 manifest 与逐文件读取路由 |
| WebEasiNote `src/App.tsx:183` | URL 全量读为 Blob 后再解析 ZIP | 增加基于 HTTP 文件源的打开入口 |
| WebEasiNote `src/courseware-loader.ts:20` | 预先创建所有资源 Blob URL、解析全部页、预热全部静态计划 | 拆为初始化、单页解析、按需资源、空闲预热 |
| WebEasiNote `src/parser.ts:180` | 枚举并按 `Slide_N` 排序加载全部页面 | 显式页目录与单页解析；不把页面 ID 当成文件名 |
| WebEasiNote `src/components/SlideViewer.tsx:146` | 触发翻页后立即回传目标页码 | 目标页真正提交后确认，并区分同页动画步进 |

ICC-CE 现有 `IPresentationSourceService` 已支持异步翻页与固定总页数，第一阶段预计不需要修改 SDK；需联调宿主对慢回调、连续输入和批注切换的实际处理。

## 3. 官方实现参考及边界

已对指定安装版本 `EasiNote5_5.2.4.10148/Main` 做静态核对：

- `CoursewarePullProvider.Download` 在启用 partial downloading 时，排除支持延迟的扩展名；保留完整文件集合与实际已下载集合。
- `PartialDownloadingDetection` 的延迟列表为 JPG/PNG/BMP/GIF/JPEG 及若干音视频类型，**不包含 XML**。因此不能据此说官方也是只下载一页 XML。
- `CoursewareBoardLoader` 先创建所有页的身份和状态，正文加载按当前页、后一页、前一页、后两页、前两页展开，其他页可通过后台 Dispatcher 排队。
- `PartialDownloadingSlideLoader` 搜索该页未下载的资源，先创建只含 ID/尺寸的临时页，资源完成后设置完整内容；有下载/加载失败和重试状态。这与本方案的“文字图形先显示，图片逐项补齐”不同。
- `CoursewarePartialDownloadingQueue` 的反编译结果显示 4 个运行槽、按文件去重、按页关联及 `JumpToDownloadQueueHead`。这是队列能力的静态观察；本次未继续验证所有调用者何时提升优先级。

研究来源及工具限制见 `D:/Code/EasiNote-Resarch/topics/lazy-loading/README.md`。以上不代表官方运行时实测结果，也未验证该实验开关在当前用户配置中是否启用。

## 4. 建议架构

```mermaid
flowchart LR
  Cloud[希沃详情接口 / CDN] --> Session[C# CloudCoursewareSession]
  Session --> Queue[优先级下载队列 / 去重]
  Queue --> Cache[按版本的散文件缓存]
  Cache --> Route[WebView2 同源文件路由]
  Route --> Source[HTTP CoursewareSource]
  Source --> Store[页目录 / 页状态 / 资源状态]
  Store --> Viewer[当前页渐进显示]
  Viewer --> Demand[翻页与可见资源需求]
  Demand --> Queue
```

**插件负责鉴权、下载、磁盘缓存和网络优先级；WebEasiNote 负责 ENBX 语义、单页解析、页面渲染和依赖发现。** 浏览器侧只接触同源会话 URL，不携带希沃 token，也不直接依赖云 CDN 的 CORS 配置。

### 4.1 统一文件源，保留现有入口

WebEasiNote 引入纯前端 `CoursewareSource`：`listEntries()`、`readText(path, signal)`、`getResourceUrl(path)`、`dispose()`。具体实现为 ZIP、文件夹与 HTTP manifest。先接通 HTTP 云源；本地 ZIP 随后复用单页解析与按需解压，不要求为了云首屏先完成本地入口迁移。

manifest 包含协议版本、会话 ID、课件版本、文件逻辑路径和大小/散列等可公开字段。`getResourceUrl` 返回可直接用于 img/video/fetch 的稳定同源 URL；对 ZIP/文件夹实现，由资源存储按需生成并管理 Blob URL。

建议路由：

```text
GET /courseware/{sessionToken}/manifest.json
GET /courseware/{sessionToken}/files/{normalizedLogicalPath}
```

`enbx:open` 增加 `protocolVersion`、`sourceKind: 'http-manifest'`、`sessionId`、`manifestUrl`；保留原本本地文件 `url` 打开方式。重复投递同一会话应复用初始化任务；失败后允许重试，不能仅凭“该 URL 见过”就永久跳过。

### 4.2 云会话与文件缓存

新增 `CloudCoursewareSession`、`CoursewareFileCache`、`PriorityDownloadQueue`，让浏览窗口在关闭/隐藏后仍可继续播放。会话由插件持有，关闭课件时取消所有会话级任务并释放资源；不能继续使用会随云浏览窗口关闭而取消的下载 CTS。

- 会话以详情响应中的实际 `cid + version` 为准，不能用可能过期的列表版本给新内容命名。
- 第一版使用每个课件版本的独立缓存目录及 manifest，避免引入数据库；跨版本按 hash 复用可后续增加。
- 下载写入 `.part`，长度/有效 MD5 校验通过后原子提交。已有文件仅存在或非零不能证明有效；未通过校验的文件不能标为完整。
- hash 非空但不是支持的散列格式时记录校验能力，不把任意字符串强行当 MD5。当前下载器的 hash 不匹配分支为空，需补为实际失败/重试处理。
- 同一路径的需求共享下载任务；单个请求取消不应取消其他消费者的共享任务，会话取消才终止其所有工作。
- 详情 URL 失效时合并一次刷新请求；先确认仍是同一版本及同一 hash。若已发生版本变化，应提示重新打开或创建新会话，不能混入旧会话。
- 路径先规范化再按 manifest 白名单查找，拒绝目录逃逸；错误响应只暴露必要状态，不回传凭证。

`WebResourceRequested` 必须通过 deferral 异步等待文件，不能在 UI 线程同步下载。延迟请求在 finally 完成 deferral；窗口销毁、会话切换后不得再访问已销毁的 WebView。图片可第一版完整下载到缓存再回流；音视频需正确处理 MIME、HEAD、单区间 Range/206/416、Content-Length，先支持从完整缓存读取区间，网络区间缓存作为后续优化。

### 4.3 页目录、页状态与正确页序

将 `SlideDescriptor[]`（身份、位置、XML 路径、预览 URL）与 `Map<slideKey, SlideData>`（已解析内容）分开。页面状态建议为 `unrequested / loading / core-ready / error`，资源状态独立管理，避免某一资源失败让整页无法翻阅。

总页数初始化后固定，不能用“当前已解析页数”代替。`Board.xml` 中的页序/ID与 `Slides/Slide_N.xml` 的对应关系必须通过格式约定、样本与 XML 内 ID 交叉验证；现有解析器虽读取 Board IDs，实际正文仍按文件名排序。第一阶段要明确索引到文件的映射规则，支持缺页、重复 ID 和非连续编号的诊断，不能未经验证假设 ID 就是文件编号；如果缺少映射，应显式采用并记录已验证的序号规则，不能为建索引先下载全部 XML。

未加载页面不是空白 `SlideData`。Viewer、缩放映射、兼容性问题收集和缩略图面板改为读取 descriptor 与状态，并只处理已加载内容。缩略图使用独立的按需 URL，不加载正文来生成预览。

### 4.4 首屏与资源逐项补齐

初始化完成后仅 `ensureSlide(initialIndex)`，解析后马上挂载正文。资源 ID 经 `Reference.xml` 映射成稳定 URL，渲染器按实际出现的资源发起请求；字体/外部资源延用独立状态。背景、图片、媒体封面、组合/表格/专题内图片等均需纳入依赖覆盖。

优先利用稳定 URL 让浏览器加载图片，避免每个资源完成后重建全局 resourceMap。图片渲染器有 onLoad/onError 和重试，尺寸沿用 XML，不随图片到达跳动。JS 需要 fetch 的 Flash 等资源也走同一文件源。媒体默认避免预载整文件；用户播放/自动播放需要时提升优先级。

补齐资源只更新资源状态，不重新挂载整个 SlideViewer，不重置元素动画、音频、转场或 ICC-CE 批注。静态缓存指纹/绘制计划只为已解析页面按需计算，移出全量首屏 await 链。

### 4.5 调度规则（以下为建议默认值，需测量调整）

| 优先级 | 内容 |
| --- | --- |
| P0 | 当前用户目标页 XML、最小元数据、用户刚触发播放的媒体 |
| P1 | 当前页可见背景/图片/封面及渲染必需资源 |
| P2 | 下一页与上一页 XML、小型资源；方向明确时偏向翻页方向 |
| P3 | 已打开缩略图面板中可见的预览 |
| P4 | 空闲时其他页 XML 与小资源 |
| P5 | 全量离线下载、空闲大媒体 |

全局初始上限 4，后台最多占 2 个槽，至少保留前台容量；无前台需求时可按策略借用。提升已排队任务应原位更新优先级并复用同一结果；对已在下载的大后台文件，限制其占槽或取消后可重试，避免 P0 被 4 个视频堵住。加入 FIFO/老化机制避免长时间饥饿。

浏览器 `requestIdleCallback` 只决定何时提交预取和解析工作；网络优先级以 C# 队列为准。首屏提交后再开始相邻页预取，用户操作到来立即降低后台优先级。后台只下载文件不等于把全部页面解析/挂载；DOM、解码图片与静态栅格仍分别受内存预算约束。

### 4.6 翻页、宿主页码与批注

新增统一异步 `navigateTo(index, source, requestId)`，所有键盘、鼠标、手势、缩略图和宿主导航入口都经过它。

1. 同页元素动画可立即确认；跨页先加载目标页 XML，保留旧页画面和批注。
2. 目标页 core-ready 后提交新页，开始转场。图片可继续补齐，不把视频全量下载作为翻页完成条件。
3. 正文提交后回传 `page-changed` / `navigation-complete`，插件再更新 ICC-CE 页码与批注。使用 `sessionId + navigationId + requestId` 防止旧响应污染新页。
4. 连续跳页采用最后目标生效；顺序前后翻页根据待处理目标计算，避免连续点击都指向同一页；被替代请求明确返回 superseded，宿主不切批注。
5. 替换插件当前固定 8 秒等待策略，增加进行中/失败/取消消息及有界超时；超时后取消该导航意图并忽略迟到提交。仅增加等待时长无法解决竞态。

另外：`Viewer.tsx` 当前在 `[slides]` 变化时清空编辑结果，`SlideViewer.tsx` 在 `[slides]` 变化时清理栅格缓存。增量加载后必须改为按文档会话切换清理，避免新页到达就清空已有工作。云课件“另存为”需补全全部清单后导出并应用编辑补丁，不得静默导出残缺课件；该操作可显示独立进度，不阻塞此前播放。

## 5. 实施顺序与完成标准

### 阶段 A：文件源与单页状态基础

WebEasiNote 增加 HTTP source、descriptor/page store、单页 ensure、加载/失败 UI 及会话隔离。先用本地静态目录和延迟响应验证，不依赖真实登录。去掉新入口的全量资源和静态计划等待。完成标准：缺少其他页时仍能显示目标页，页数正确，后台更新不重置当前页状态。

### 阶段 B：插件云播放闭环（首个可交付版本）

实现云会话、散文件缓存、异步同源路由、带前台容量的下载队列，区分“播放”与“完整下载”。接上宿主导航确认与批注切换。A+B 一起达到用户目标：其他 XML/媒体被人为延迟时，首张 XML 仍能先展示；图片延迟后原位补齐。

### 阶段 C：预取与可靠性

相邻页/可见缩略图预取、失败重试、断网恢复、URL 刷新、缓存校验与完整下载共享任务。加入资源预算、清理策略与正确媒体 Range。不要在缺失缓存时把课件显示为“已完整下载”。

### 阶段 D：本地入口与性能优化

本地 ENBX/文件夹复用 source/store，减少全量解压与页面解析。根据测量决定是否将超大 XML 解析移至 Worker（当前 DOMParser/解析器需要先适配），以及是否增加分块媒体缓存、跨版本 hash 复用。云首屏不等待这些优化。

## 6. 验证清单

- 自动化：相同文件并发去重与提权、共享取消、原子缓存与错误 hash、manifest 路径边界、版本变化/URL 刷新、Range 响应。
- 前端集成：仅元数据+首张 XML 可成功打开；其他请求挂起不影响首屏；资源到达不重置动画；下载失败可重试；乱序导航不会提交旧页；切课件后旧任务不更新新课件。
- 宿主联调：慢网超过 8 秒、连续翻页、动画步进、缩略图跳页、退出/重新打开、批注不串页。
- 内容回归：纯文本/图形、图片背景、嵌套组合图片、表格/专题图片、音视频、Flash、多尺寸页面、缺页及损坏资源。
- 性能记录：点击打开→manifest、元数据、首张 XML、core-ready、visual-ready；首屏前实际下载字节/文件数；缓存命中；翻页 P50/P95；峰值队列与内存。
- 关键验收：给第 2 页及以后 XML、非当前页资源加长延迟，首屏不得等待它们；给首屏图片加延迟，正文须先出现并随后补齐。当前页 P0 请求不被后台大媒体耗尽并发槽。

本次没有运行真实云课件网络实验，因此不预设绝对首屏秒数；用相同课件、缓存状态与网络条件比较改造前后结果，并记录首屏关键路径中实际下载了哪些文件。
