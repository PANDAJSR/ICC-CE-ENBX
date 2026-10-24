# 课件网页弹窗的登录态

课件资源卡通过 WebEasiNote 的 `ResourceCardRenderer` 打开 `displayUrl`，插件在 `NewWindowRequested` 中创建独立 WPF/WebView2 窗口。登录窗口使用独立隐私会话，原实现没有把已保存的希沃账号传给播放器，弹窗共享播放器 Environment 也无法继承登录窗口的 Cookie。

## 官方实现依据

分析版本：EasiNote5 5.2.4.10148，`Main/EasiNote.UI.dll`。

- SHA-256：`2207f19ba7b236f2f6adb6235581cad2f29f1e64c534aeb7db24feb97f5abc71`
- MVID：`09b48943-a25a-4376-a0ca-d0a0aec27ec3`
- REA 成员证据：`ev_cdd7a2e0fa5a3125f6a06885223b344100f4f66821d4368a295a614cb8582570`
- REA 字符串证据：`ev_3ed647ffaa47ab55990bb1128a3b817d6e3ceb6b37775d40b66e1a9685165c72`
- 结合本机 ILSpy 11.1.0.9782 对 `Cvte.EasiNote.UI.BrowserEnvironment` 的 C# 重建阅读；这是静态分析，没有观察服务器实际接受凭证。

`InjectCookies` 在初始化时清理旧认证 Cookie，写入当前账号，并订阅 token 更新和退出事件。`SetCefCookieAsync` 对 `seewo.com`、`easinote.flashshan.com`、`live-t.imlizhi.com`、`seewoedu.cn` 写入 HttpOnly Cookie：

| Cookie | 官方取值 |
| --- | --- |
| x-auth-token、x-token | CurrentUserToken |
| x-auth-app | CustomText.WebAppCode |
| x-auth-brand | 当前发行版 WebAppBrand |
| phone | CurrentUserId |
| app-versions | 官方客户端版本 |
| x-custom-version、x-special-version | 定制版/启动模式 |

插件复用当前账号的 token，写入两个认证 Cookie 和标准版的 `EasiNote5` / `seewo`，有手机号时同步 `phone`。不伪造官方客户端版本、定制版或资源授权。仅向上述官方配置域设置 Cookie，不向任意 URL 追加 token。

## 生命周期

- 首次弹窗导航交接前完成 Cookie 同步，弹窗与播放器共用 Environment。
- 登录、切换账号、更新凭证和退出时关闭已有课件网页弹窗，并清除上述域及子域的旧会话 Cookie，再写入当前有效账号。
- 初始化前发生的账号变更延迟到弹窗打开时处理。
- 凭证不写入前端消息、URL 或诊断日志。

## 错误的含义与验证边界

“身份验证已过期，请尝试重新登录”与缺失/失效登录态一致；“暂无使用权限”也可能由账号未购买资源、未加入授权组织或服务端策略导致，不能只凭截图断言。

插件网页登录 token 是否被每个第三方资源服务接受仍需实测，云课件接口能访问不能证明所有资源服务接受相同 token。部分页面还可能需要官方 JS 桥或额外 SSO，本次不声称已覆盖。

验证：`dotnet build ICC.CE.ENBX.csproj --no-restore -p:CreateIcpx=false -v:minimal`，0 警告、0 错误。尚未使用截图中的课件和真实账号进行端到端验证。
