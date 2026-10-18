using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ICC.CE.ENBX.Seewo.Models;
using Microsoft.Web.WebView2.Core;

namespace ICC.CE.ENBX.Seewo.Services;

public sealed class SeewoAuthManager
{
    private static readonly object StorageLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configDirectory;
    private readonly string _authFilePath;
    private SeewoAuthProfile? _currentProfile;
    private readonly List<SeewoAuthProfile> _accounts = new();
    public IReadOnlyList<SeewoAuthProfile> Accounts => _accounts.AsReadOnly();
    private string AccountsPath => Path.Combine(_configDirectory, "seewo_accounts.dat");

    private sealed class AccountStore
    {
        public List<SeewoAuthProfile> Accounts { get; set; } = new();
        public string? ActiveAccountId { get; set; }
    }

    public event Action<SeewoAuthProfile?>? AuthStateChanged;

    public SeewoAuthManager(string configDirectory)
    {
        _configDirectory = configDirectory;
        _authFilePath = Path.Combine(configDirectory, "seewo_auth.json");
        Load();
    }

    public SeewoAuthProfile? CurrentProfile => _currentProfile;

    public bool IsLoggedIn => _currentProfile != null && _currentProfile.IsValid;

    public void Load()
    {
        try
        {
            if (File.Exists(AccountsPath))
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(AccountsPath), null, DataProtectionScope.CurrentUser);
                var store = JsonSerializer.Deserialize<AccountStore>(bytes, JsonOptions);
                _accounts.Clear();
                _accounts.AddRange(store?.Accounts ?? new());
                _currentProfile = _accounts.FirstOrDefault(x => x.AccountId == store?.ActiveAccountId);
                return;
            }
            if (File.Exists(_authFilePath))
            {
                var json = File.ReadAllText(_authFilePath, Encoding.UTF8);
                _currentProfile = JsonSerializer.Deserialize<SeewoAuthProfile>(json, JsonOptions);
                if (_currentProfile?.Token == "user_authenticated") _currentProfile = null;
                if (_currentProfile != null)
                {
                    _accounts.Add(_currentProfile);
                    Persist();
                    WithHostWriteAccess(_configDirectory, () => File.Delete(_authFilePath));
                }
            }
        }
        catch
        {
            _currentProfile = null;
        }
    }

    public void Save(SeewoAuthProfile profile)
    {
        var existing = _accounts.FirstOrDefault(x => x.AccountId == profile.AccountId ||
            (!string.IsNullOrWhiteSpace(profile.Uid) && x.Uid == profile.Uid));
        if (existing != null && !ReferenceEquals(existing, profile))
        {
            profile.AccountId = existing.AccountId;
            profile.Remark = existing.Remark;
            _accounts.Remove(existing);
        }
        if (!_accounts.Contains(profile)) _accounts.Add(profile);
        _currentProfile = profile;
        Persist();
        AuthStateChanged?.Invoke(_currentProfile);
    }

    public void UpdateProfile(SeewoAuthProfile profile)
    {
        if (!_accounts.Contains(profile)) return;
        if (!string.IsNullOrWhiteSpace(profile.Uid))
        {
            foreach (var duplicate in _accounts.Where(x => !ReferenceEquals(x, profile) && x.Uid == profile.Uid).ToList())
            {
                if (string.IsNullOrWhiteSpace(profile.Remark)) profile.Remark = duplicate.Remark;
                if (ReferenceEquals(_currentProfile, duplicate)) _currentProfile = profile;
                _accounts.Remove(duplicate);
            }
        }
        Persist();
        AuthStateChanged?.Invoke(_currentProfile);
    }

    public void Activate(SeewoAuthProfile profile)
    {
        if (!_accounts.Contains(profile)) throw new InvalidOperationException("账号不存在");
        var previous = _currentProfile;
        var previousTime = profile.LastUsedTime;
        profile.LastUsedTime = DateTime.UtcNow;
        _currentProfile = profile;
        try { Persist(); }
        catch
        {
            _currentProfile = previous;
            profile.LastUsedTime = previousTime;
            throw;
        }
        AuthStateChanged?.Invoke(profile);
    }

    public void Remove(SeewoAuthProfile profile)
    {
        _accounts.Remove(profile);
        if (ReferenceEquals(_currentProfile, profile)) _currentProfile = null;
        Persist();
        AuthStateChanged?.Invoke(_currentProfile);
    }

    private void Persist()
    {
        lock (StorageLock)
        {
            WithHostWriteAccess(_configDirectory, PersistCore);
        }
    }

    private static void WithHostWriteAccess(string path, Action action)
    {
        // 宿主保护会持有目录及临时文件的禁止删除句柄；整个事务必须在同一写入门闩中执行。
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Ink_Canvas.Helpers.ProcessProtectionManager", false))
            .FirstOrDefault(candidate => candidate != null);
        var method = type?.GetMethod("WithWriteAccess", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string), typeof(Action) }, null);
        if (method == null)
        {
            action();
            return;
        }
        var executed = false;
        try
        {
            method.Invoke(null, new object[] { path, (Action)(() => { executed = true; action(); }) });
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
        if (!executed) throw new IOException("宿主未允许写入希沃账号配置，请稍后重试。");
    }

    private void PersistCore()
    {
        Directory.CreateDirectory(_configDirectory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new AccountStore
        {
            Accounts = _accounts,
            ActiveAccountId = _currentProfile?.AccountId
        }, JsonOptions);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        // 每次写入使用独立临时文件，避免多个窗口/实例争用同一个 .tmp。
        var temporaryPath = AccountsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RetryFileAccess(() => File.WriteAllBytes(temporaryPath, encrypted));
            RetryFileAccess(() => File.Move(temporaryPath, AccountsPath, true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"保存希沃账号失败：{AccountsPath}。请查看诊断日志中的占用或权限错误。", ex);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RetryFileAccess(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 5)
            {
                // Windows 扫描器可能在关闭写句柄后短暂持有文件；只重试共享/锁冲突。
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                // Windows 覆盖重命名遇到不允许删除的共享句柄时也可能返回 AccessDenied。
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public void Logout()
    {
        _currentProfile = null;
        Persist();
        AuthStateChanged?.Invoke(null);
    }

    /// <summary>
    /// 从 WebView2 CoreCookieManager 中提取希沃相关 Cookie 并生成 AuthProfile
    /// </summary>
    public async Task<SeewoAuthProfile?> TryExtractFromWebViewAsync(CoreWebView2CookieManager cookieManager, string? fallbackToken = null)
    {
        try
        {
            var cookies = await cookieManager.GetCookiesAsync("https://edu.seewo.com");
            var idCookies = await cookieManager.GetCookiesAsync("https://id.seewo.com");
            var rootCookies = await cookieManager.GetCookiesAsync("https://seewo.com");

            var allCookies = cookies.Concat(idCookies).Concat(rootCookies)
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var tokenCookie = allCookies.FirstOrDefault(c => c.Name.Equals("x-token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
                ?? allCookies.FirstOrDefault(c => c.Name.Equals("x-auth-token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
                ?? allCookies.FirstOrDefault(c => c.Name.Equals("x-samesite-none-token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
                ?? allCookies.FirstOrDefault(c => c.Name.Equals("token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
                ?? allCookies.FirstOrDefault(c => c.Name.Equals("auth_token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
                ?? allCookies.FirstOrDefault(c => c.Name.Equals("seewo-token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value));

            string tokenValue = tokenCookie?.Value ?? fallbackToken ?? string.Empty;

            if (string.IsNullOrWhiteSpace(tokenValue) && allCookies.Count == 0)
            {
                return null;
            }

            var profile = new SeewoAuthProfile
            {
                Token = tokenValue,
                LoginTime = DateTime.UtcNow
            };

            foreach (var cookie in allCookies)
            {
                profile.Cookies[cookie.Name] = cookie.Value;
                if (cookie.Name.Equals("uid", StringComparison.OrdinalIgnoreCase))
                    profile.Uid = cookie.Value;
                else if (cookie.Name.Equals("username", StringComparison.OrdinalIgnoreCase))
                    profile.UserName = Uri.UnescapeDataString(cookie.Value);
                else if (cookie.Name.Equals("nickname", StringComparison.OrdinalIgnoreCase))
                    profile.NickName = Uri.UnescapeDataString(cookie.Value);
                else if (cookie.Name.Equals("phone", StringComparison.OrdinalIgnoreCase))
                    profile.Phone = cookie.Value;
            }

            // 尝试从 JWT Token 中解码载荷
            TryEnrichProfileFromJwt(profile);

            return profile;
        }
        catch
        {
            return null;
        }
    }

    private static void TryEnrichProfileFromJwt(SeewoAuthProfile profile)
    {
        try
        {
            var parts = profile.Token.Split('.');
            if (parts.Length >= 2)
            {
                var payload = parts[1];
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
                var json = Encoding.UTF8.GetString(bytes);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (string.IsNullOrWhiteSpace(profile.Uid) && root.TryGetProperty("uid", out var uidProp))
                    profile.Uid = uidProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(profile.UserName) && root.TryGetProperty("username", out var userProp))
                    profile.UserName = userProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(profile.NickName) && root.TryGetProperty("name", out var nameProp))
                    profile.NickName = nameProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(profile.Phone) && root.TryGetProperty("phone", out var phoneProp))
                    profile.Phone = phoneProp.GetString() ?? "";
            }
        }
        catch
        {
            // JWT 解析非关键失败，忽略
        }
    }
}
