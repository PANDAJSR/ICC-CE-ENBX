using System.IO;
using System.Text;
using System.Text.Json;
using ICC.CE.ENBX.Seewo.Models;
using Microsoft.Web.WebView2.Core;

namespace ICC.CE.ENBX.Seewo.Services;

public sealed class SeewoAuthManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configDirectory;
    private readonly string _authFilePath;
    private SeewoAuthProfile? _currentProfile;

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
            if (File.Exists(_authFilePath))
            {
                var json = File.ReadAllText(_authFilePath, Encoding.UTF8);
                _currentProfile = JsonSerializer.Deserialize<SeewoAuthProfile>(json, JsonOptions);
            }
        }
        catch
        {
            _currentProfile = null;
        }
    }

    public void Save(SeewoAuthProfile profile)
    {
        _currentProfile = profile;
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            File.WriteAllText(_authFilePath, json, Encoding.UTF8);
        }
        catch
        {
            // 忽略写入异常
        }
        AuthStateChanged?.Invoke(_currentProfile);
    }

    public void Logout()
    {
        _currentProfile = null;
        try
        {
            if (File.Exists(_authFilePath))
            {
                File.Delete(_authFilePath);
            }
        }
        catch
        {
        }
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

            Save(profile);
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
