using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace DataSync.Common.Ocr.Internal;

public sealed class OcrSourceResolver
{
    private const int MaxRedirectCount = 5;
    private readonly HttpClient _client;
    private readonly OcrRuntimeOptions _runtimeOptions;

    public OcrSourceResolver(IOptions<OcrRuntimeOptions> runtimeOptions)
    {
        _runtimeOptions = runtimeOptions.Value;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectAsync
        });
    }

    internal async Task<ResolvedOcrSource> ResolveAsync(
        OcrSource source,
        OcrConversionOptions options,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            throw new InvalidOperationException("OCR 输入来源为空。");

        return source.Kind switch
        {
            OcrSourceKind.FilePath => ResolveFilePath(source.Value, options),
            OcrSourceKind.Url => await ResolveUrlAsync(source.Value, options, workDirectory, cancellationToken),
            OcrSourceKind.Base64 => await ResolveBase64Async(source.Value, options, workDirectory, cancellationToken),
            _ => throw new InvalidOperationException($"不支持的 OCR 来源类型：{source.Kind}")
        };
    }

    private ResolvedOcrSource ResolveFilePath(string path, OcrConversionOptions options)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureAllowedPath(fullPath, options.AllowedFileRoots, resolveExistingRoot: false);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("OCR 输入 PDF 不存在。", fullPath);

        var resolvedPath = ResolveExistingPath(fullPath);
        EnsureAllowedPath(resolvedPath, options.AllowedFileRoots, resolveExistingRoot: true);
        EnsureSize(resolvedPath, options.MaxInputBytes);
        return new ResolvedOcrSource(resolvedPath, false);
    }

    private async Task<ResolvedOcrSource> ResolveUrlAsync(
        string url,
        OcrConversionOptions options,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("OCR URL 来源必须是 http 或 https 绝对地址。");

        EnsureAllowedHost(uri);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _runtimeOptions.UrlTimeoutSeconds)));

        var localPath = Path.Combine(workDirectory, "source.pdf");
        await DownloadUrlAsync(uri, localPath, options.MaxInputBytes, timeoutCts.Token);

        return new ResolvedOcrSource(localPath, true);
    }

    private static async Task<ResolvedOcrSource> ResolveBase64Async(
        string base64,
        OcrConversionOptions options,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var value = NormalizeBase64Payload(base64);
        EnsureBase64Size(value, options.MaxInputBytes);

        var bytes = Convert.FromBase64String(value);
        if (options.MaxInputBytes.HasValue && bytes.LongLength > options.MaxInputBytes.Value)
            throw new InvalidOperationException($"OCR 输入 PDF 超过大小限制：{bytes.LongLength} > {options.MaxInputBytes.Value}");

        var localPath = Path.Combine(workDirectory, "source.pdf");
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
        return new ResolvedOcrSource(localPath, true);
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long? maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read == 0)
                break;

            total += read;
            if (maxBytes.HasValue && total > maxBytes.Value)
                throw new InvalidOperationException($"OCR 输入 PDF 超过大小限制：{total} > {maxBytes.Value}");

            await destination.WriteAsync(buffer, 0, read, cancellationToken);
        }
    }

    private static void EnsureSize(string path, long? maxBytes)
    {
        if (!maxBytes.HasValue)
            return;

        var length = new FileInfo(path).Length;
        if (length > maxBytes.Value)
            throw new InvalidOperationException($"OCR 输入 PDF 超过大小限制：{length} > {maxBytes.Value}");
    }

    private static string NormalizeBase64Payload(string base64)
    {
        var value = base64.Trim();
        var commaIndex = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            value = value[(commaIndex + 1)..];

        if (!value.Any(char.IsWhiteSpace))
            return value;

        return new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static void EnsureBase64Size(string value, long? maxBytes)
    {
        if (!maxBytes.HasValue)
            return;

        var padding = value.EndsWith("==", StringComparison.Ordinal)
            ? 2
            : value.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
        var estimatedBytes = ((long)value.Length + 3) / 4 * 3 - padding;
        if (estimatedBytes > maxBytes.Value)
            throw new InvalidOperationException($"OCR 输入 PDF 超过大小限制：{estimatedBytes} > {maxBytes.Value}");
    }

    private static void EnsureAllowedPath(
        string fullPath,
        IReadOnlyList<string> allowedRoots,
        bool resolveExistingRoot)
    {
        if (allowedRoots.Count == 0)
            throw new InvalidOperationException("OCR 文件路径来源未配置允许目录。");

        var allowed = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root))
            .Any(root => IsPathUnderRoot(fullPath, root, resolveExistingRoot));

        if (!allowed)
            throw new InvalidOperationException("OCR 输入 PDF 路径不在允许目录内。");
    }

    private void EnsureAllowedHost(Uri uri)
    {
        var allowedHosts = SplitAllowedHosts(_runtimeOptions.AllowedUrlHosts);
        if (allowedHosts.Count == 0)
            throw new InvalidOperationException("OCR URL 来源未配置允许的 Host 白名单。");

        if (!allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("OCR URL 来源不在允许的 Host 白名单内。");
    }

    private static IReadOnlyList<string> SplitAllowedHosts(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException("OCR URL 来源无法连接到允许的 IP 地址。", lastException);
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveAllowedAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0)
            throw new InvalidOperationException("OCR URL 来源 Host 未解析到任何 IP 地址。");

        var allowedCidrs = SplitAllowedCidrs(_runtimeOptions.AllowedUrlCidrs)
            .Select(ParseIpNetwork)
            .ToList();
        var normalizedAddresses = addresses.Select(NormalizeAddress).Distinct().ToList();
        foreach (var address in normalizedAddresses)
        {
            var allowed = allowedCidrs.Count > 0
                ? allowedCidrs.Any(network => network.Contains(address))
                : !IsSpecialAddress(address);
            if (!allowed)
                throw new InvalidOperationException("OCR URL 来源解析到不允许访问的 IP 地址。");
        }

        return normalizedAddresses;
    }

    private static IReadOnlyList<string> SplitAllowedCidrs(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IpNetwork ParseIpNetwork(string value)
    {
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var address))
            throw new InvalidOperationException($"OCR URL CIDR 配置不是有效 IP 地址：{value}");

        address = NormalizeAddress(address);
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefixLength = maxPrefix;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefixLength) || prefixLength < 0 || prefixLength > maxPrefix))
            throw new InvalidOperationException($"OCR URL CIDR 配置前缀长度无效：{value}");

        return new IpNetwork(address, prefixLength);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsSpecialAddress(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                || (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6Multicast
                || address.IsIPv6SiteLocal
                || (bytes[0] & 0xfe) == 0xfc
                || IsInNetwork(address, "::", 96)
                || IsInNetwork(address, "64:ff9b::", 96)
                || IsInNetwork(address, "64:ff9b:1::", 48)
                || IsInNetwork(address, "100::", 64)
                || IsInNetwork(address, "2001::", 32)
                || IsInNetwork(address, "2001:2::", 48)
                || IsInNetwork(address, "2001:10::", 28)
                || IsInNetwork(address, "2001:20::", 28)
                || IsInNetwork(address, "2001:db8::", 32)
                || IsInNetwork(address, "2002::", 16)
                || IsInNetwork(address, "3fff::", 20);
        }

        return true;
    }

    private static bool IsInNetwork(IPAddress address, string networkAddress, int prefixLength)
        => new IpNetwork(IPAddress.Parse(networkAddress), prefixLength).Contains(address);

    private async Task DownloadUrlAsync(
        Uri initialUri,
        string localPath,
        long? maxBytes,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= MaxRedirectCount; redirectCount++)
        {
            EnsureHttpUri(currentUri);
            EnsureAllowedHost(currentUri);

            using var response = await _client.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == MaxRedirectCount)
                    throw new InvalidOperationException($"OCR URL 重定向次数超过限制：{MaxRedirectCount}");

                currentUri = ResolveRedirectUri(currentUri, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var local = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await CopyWithLimitAsync(remote, local, maxBytes, cancellationToken);
            return;
        }

        throw new InvalidOperationException("OCR URL 下载失败。");
    }

    private static void EnsureHttpUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("OCR URL 来源必须是 http 或 https 绝对地址。");
    }

    private static Uri ResolveRedirectUri(Uri currentUri, Uri? location)
    {
        if (location == null)
            throw new InvalidOperationException("OCR URL 重定向缺少 Location。");

        return location.IsAbsoluteUri
            ? location
            : new Uri(currentUri, location);
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return value is 301 or 302 or 303 or 307 or 308;
    }

    private static bool IsPathUnderRoot(string fullPath, string root, bool resolveExistingRoot)
    {
        var resolvedRoot = resolveExistingRoot && (Directory.Exists(root) || File.Exists(root))
            ? ResolveExistingPath(root)
            : Path.GetFullPath(root);
        var relativePath = Path.GetRelativePath(resolvedRoot, fullPath);
        return relativePath == "."
            || (!relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                && relativePath != ".."
                && !Path.IsPathRooted(relativePath));
    }

    private static string ResolveExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return fullPath;

        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
            return fullPath;

        foreach (var part in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            current = ResolveLinkTarget(current);
        }

        return Path.GetFullPath(current);
    }

    private static string ResolveLinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target == null
            ? path
            : Path.GetFullPath(target.FullName);
    }
}

internal sealed record ResolvedOcrSource(string LocalPath, bool IsTemporary);

internal sealed record IpNetwork(IPAddress Address, int PrefixLength)
{
    public bool Contains(IPAddress address)
    {
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (address.AddressFamily != Address.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = Address.GetAddressBytes();
        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xff << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
