using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ICC.CE.ENBX.Seewo.Services;

internal sealed class MobileImageDownload : IDisposable
{
    internal const long MaxBytes = 50 * 1024 * 1024;
    private readonly HttpClient _http;
    public MobileImageDownload()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                foreach (var address in addresses.Where(IsPublicAddress))
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); }
                }
                throw new HttpRequestException("图片地址没有可用的公网连接。");
            }
        }) { Timeout = TimeSpan.FromMinutes(2) };
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20; // global unicast only
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    internal static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Port != 443 || uri.UserInfo.Length > 0 || uri.HostNameType == UriHostNameType.Unknown ||
            (IPAddress.TryParse(uri.Host, out var ip) && !IsPublicAddress(ip)))
            throw new InvalidDataException("图片下载地址必须是公网 HTTPS 地址。");
        return uri;
    }

    public async Task<string> DownloadAsync(string url, string directory, string? archiveImageName, string? extension, CancellationToken ct)
    {
        var uri = ValidateUrl(url);
        Directory.CreateDirectory(directory);
        var downloaded = Path.Combine(directory, "download");
        for (var redirect = 0; ; redirect++)
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirect >= 4 || response.Headers.Location == null) throw new HttpRequestException("图片重定向过多。");
                uri = ValidateUrl(new Uri(uri, response.Headers.Location).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("图片下载超过 50 MiB。");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = File.Create(downloaded);
            await CopyLimitedAsync(source, destination, MaxBytes, ct);
            break;
        }
        if (archiveImageName != null) return await ExtractAsync(downloaded, directory, archiveImageName, ct);
        var ext = NormalizeExtension(extension ?? Path.GetExtension(uri.AbsolutePath));
        var path = Path.Combine(directory, "image" + ext);
        File.Move(downloaded, path);
        return path;
    }

    internal static string NormalizeExtension(string? extension)
    {
        var ext = "." + (extension ?? "").TrimStart('.').ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" or ".webp"))
            throw new NotSupportedException("不支持的手机图片格式。");
        return ext;
    }

    internal static async Task<string> ExtractAsync(string archive, string directory, string name, CancellationToken ct)
    {
        // Never use a sender-controlled name as a destination path, or extract the complete archive.
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            throw new InvalidDataException("无效的图片文件名。");
        var ext = NormalizeExtension(Path.GetExtension(name));
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 100) throw new InvalidDataException("图片压缩包条目过多。");
        var matches = zip.Entries.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Length > MaxBytes) throw new InvalidDataException("压缩包图片缺失、重复或过大。");
        var output = Path.Combine(directory, "image" + ext);
        using var source = matches[0].Open();
        await using var target = File.Create(output);
        await CopyLimitedAsync(source, target, MaxBytes, ct);
        return output;
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("图片数据超过大小限制。");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
    public void Dispose() => _http.Dispose();
}
