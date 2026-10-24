# SeewoImBridge

独立 `net10.0-windows` / `win-x86`、framework-dependent 辅助进程，直接使用本机官方混合 C++/CLI `IMWrapper.dll`，不引用 `Cvte.InstantMessaging` 或 `Cvte.Core`。父进程负责授权账号、进程生命周期、下载、插入图片和业务回执。本目录不改变主项目的编译设置。

## 构建与运行

```powershell
dotnet build tools/SeewoImBridge/SeewoImBridge.csproj -c Release
# 其它官方安装目录（Main 目录）
dotnet build tools/SeewoImBridge/SeewoImBridge.csproj -c Release '-p:SeewoInstallDirectory=C:\path\Main'
tools/SeewoImBridge/bin/Release/net10.0-windows/win-x86/SeewoImBridge.exe --sdk 'C:\path\Main' --probe
```

默认编译和运行 SDK 目录均为 `C:\Program Files (x86)\Seewo\EasiNote5\EasiNote5_5.2.4.10148\Main`。运行时 `--sdk` 独立于 MSBuild 属性；更改构建属性不会把新路径嵌入程序。需要 Windows 和 x86 .NET 10 runtime，不能用 x64 `dotnet.exe` 启动 DLL；使用产出的 x86 exe，或显式 x86 dotnet。

输出目录：`tools/SeewoImBridge/bin/Release/net10.0-windows/win-x86/`。父项目集成时复制这里的 `SeewoImBridge.exe`、`.dll`、`.deps.json`、`.runtimeconfig.json`；PDB 可选。禁止把官方库加入仓库或发布包；本项目 Reference 设置 `Private=false`。运行时通过默认 AssemblyLoadContext 的 resolver 从安装目录加载托管依赖，通过 Windows `AddDllDirectory` 从 wrapper 目录、安装的 win-x86/native 和 Main 加载 native 依赖。官方 `IMWrapper.dll` 位于 `Main/runtimes/win-x86/lib/net5.0/`，其 `Ijwhost.dll`、`rtmq_im.dll`、`rtmq_base.dll` 等保留在原安装位置。

`--probe` 只加载依赖、调用 native `GetIMSdkVersion` 并列出已加载的相关模块，不注册回调、不调用 InitIM、不连接账号。2026-10-23 本机 .NET SDK 10.0.301、x86 runtime 10.0.9 实测成功；官方版本 getter 返回空字符串，因此 `sdkVersion:""` 是实际结果，不能当成版本号已核实。完整离线检查：

```powershell
python tools/SeewoImBridge/smoke.py
# 或指定安装目录
python tools/SeewoImBridge/smoke.py 'C:\path\Main'
```

## JSONL 协议

stdin/stdout 使用 UTF-8，每行一个严格 JSON 对象（JSON 属性和值必须使用双引号），每条输入必须以换行结尾；父进程应及时 flush 并持续读取 stdout、stderr。单行限制为 1,048,576 个 UTF-16 字符。stdout 仅输出协议 JSON；SDK Console/native 标准输出重定向至 stderr，SDK 日志回调丢弃。错误和断连文本使用固定描述，不回显 SDK 原始消息、凭证或输入内容；stderr 只用于本地诊断，父进程不应把它当成协议或保存为带凭证的日志。

输入：

```json
{"type":"connect","uid":"<resource uid>","token":"<official account token>","deviceId":"<persistent device id>","en5Id":"<session id>","machineName":"<display name>"}
{"type":"send","id":302,"body":{"code":0},"platform":2}
{"type":"stop"}
```

一次进程只允许一次完整 connect；账号切换或失败重试需要重启进程。所有 connect 字段都是非空字符串；deviceId 由父进程持久化，en5Id 在当前账号会话内固定。输入不完整时拒绝处理，不调用 InitIM。初始化返回成功不等于 connected；send 必须等 connected。省略 platform 时向 2、3、9、13、14、15、16 同时发送，指定时只允许这些平台。目标为当前同一 UID / 官方 AppId；发送包装为 `action=Mobile_Message`、`content={id,en5Id,body}`，ttl=0、flag=0。send 成功无额外 IPC 回复；SDK 报错输出 error，不能将没有 error 等同于手机业务处理成功。

输出：

```json
{"type":"connected"}
{"type":"disconnected","code":0,"message":"IM transport disconnected."}
{"type":"message","id":303,"body":{"address":"...","imagename":"..."},"en5Id":"<target session or null>","uid":"<sender uid>","appId":"29a414ccedf245208cd78ef76954e73d","platform":2,"messageId":"<SDK message id>"}
{"type":"error","message":"<sanitized description>"}
```

`--probe` 另输出一行 `{type:"probe",ok:true,architecture:"x86",sdkVersion:"",modules:[...]}` 后退出。启动失败退出码 1；stop 和 EOF 正常退出码 0。EOF 丢弃末尾未换行的输入并关闭 IM，stop 无确认事件。原生 SDK 调用可能阻塞，父进程必须有退出超时与最终终止策略。事件在单一消费者上串行处理，native 回调只快照并入队，所有桥端回调异常被捕获；256 条事件队列溢出会报告错误并关闭会话。无法防止官方 wrapper/native 内部的访问冲突或其托管回调之前发生的异常，独立进程提供故障隔离。

## 身份与业务处理边界

桥内先检查来源 UID 精确等于当前 UID、AppId 精确等于官方 AppId、来源平台在支持列表、action 为 Mobile_Message。非空 en5Id 必须精确匹配当前会话；空、缺少或 null en5Id 按官方行为允许，通过 IPC 传递 null 或空字符串，父进程可进一步收紧策略。只向父进程转发通过检查的消息，携带全部来源字段。父进程仍须按活动账号/会话丢弃旧进程输出，并校验下载 URL、大小和解压路径。桥不自动回应 301/303/501；回执由父进程按实际插入结果发送。

连接参数为 `mc.seewo.com` / AppId `29a414ccedf245208cd78ef76954e73d` / appcode `EasiNote5` / platform 1 / protocol 23。device_id 为 `deviceId_uid`；device_token 与 user.token 都使用 connect token。extras 为 appVersion `5.2.4.10148`、protocolVersion 23、当前 Unix 毫秒、en5Id、name、osInfo。`loginTimestamp` 的毫秒单位现已通过本机 `EasiNote.Utils.dll` 中 `Cvte.EasiNote.Utils.DateTimeExtension.ToTimeStamp` 核实：`(dateTime - new DateTime(1970,1,1)).TotalMilliseconds`。桥使用本地 UTC，不查询官方服务器时间。

connected 后自动广播 903（version 的 major/minor/build/revision = 5/2/4/10148、protocol=23、clientName=machineName）及 904（空 body）。收到合法 902 时向其来源平台回 903、904，并仍向父进程转发原始 902；父进程无需重复回应。重连的 connected 回调也会重新广播。903 的结构及 902 应答已在本机 `MobileMessageRegister.RequestSoftwareVersion`、`MobileCommunicationAdapter.SendSoftWareInfo` 核实；官方还在移动端 DistanceConnectStatChanged 时发送 903，本桥不解析该通知，后上线的手机可通过 902 获取信息。

904 目前只确认 `PcReady : MobileMessage`、消息号 904、无额外字段。对研究目录完整反编译 `EasiNote.MobileLessons.cs` 未找到构造发送位置，重点检查 MobileCommunicationStartup、MobileMessageRegister、MobileManager 也未发现。因此本桥在 connected/902 后发送 904 是用户要求的互通策略，尚不能声称复现官方发送时机。

离线检查不证明网页登录 token 被生产 IM 接受、手机显示设备、手机接收广播或图片业务成功；这些需要父会话在用户授权账号下联调。桥不实现官方课件状态同步、DistanceLogin 平台通知、图片下载或任何播放器操作，也不主动启动本机 HTTP/socket 服务。
