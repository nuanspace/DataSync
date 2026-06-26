using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataSync.Common.Ocr.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataSync.Common.Ocr;

/// <summary>
/// 基于 pdftoppm 与 Tesseract CLI 的 PDF OCR 转换服务。
/// </summary>
public sealed class OcrConversionService : IOcrConversionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly OcrRuntimeOptions _runtimeOptions;
    private readonly OcrSourceResolver _sourceResolver;
    private readonly PdfToPpmRenderer _renderer;
    private readonly TesseractCliOcrEngine _ocrEngine;
    private readonly ILogger<OcrConversionService> _logger;

    public OcrConversionService(
        IOptions<OcrRuntimeOptions> runtimeOptions,
        OcrSourceResolver sourceResolver,
        PdfToPpmRenderer renderer,
        TesseractCliOcrEngine ocrEngine,
        ILogger<OcrConversionService> logger)
    {
        _runtimeOptions = runtimeOptions.Value;
        _sourceResolver = sourceResolver;
        _renderer = renderer;
        _ocrEngine = ocrEngine;
        _logger = logger;
    }

    public async Task<OcrDocumentResult> ConvertAsync(
        OcrSource source,
        OcrConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.Now;
        var workDirectory = CreateWorkDirectory();
        try
        {
            var resolved = await _sourceResolver.ResolveAsync(source, options, workDirectory, cancellationToken);
            var imagePaths = await _renderer.RenderAsync(resolved.LocalPath, workDirectory, options, cancellationToken);
            var pages = new List<OcrPageResult>();
            for (var index = 0; index < imagePaths.Count; index++)
            {
                pages.Add(await _ocrEngine.ReadAsync(imagePaths[index], index + 1, options, cancellationToken));
            }

            return new OcrDocumentResult
            {
                SourceKind = source.Kind,
                Language = options.Language,
                Dpi = options.Dpi,
                PageSegMode = options.PageSegMode,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.Now,
                PageCount = pages.Count,
                Pages = pages,
                TextItems = pages.SelectMany(page => page.TextItems).ToList(),
                FullText = string.Join($"{Environment.NewLine}{Environment.NewLine}", pages.Select(page => page.Text)),
                Metadata =
                {
                    ["Engine"] = "tesseract-cli",
                    ["Renderer"] = "pdftoppm"
                }
            };
        }
        finally
        {
            if (!options.KeepWorkFiles)
                TryDeleteDirectory(workDirectory);
            else
                _logger.LogInformation("OCR 临时文件已保留：{WorkDirectory}", workDirectory);
        }
    }

    public async Task<OcrDocumentResult> ConvertToJsonFileAsync(
        OcrSource source,
        OcrConversionOptions options,
        string outputJsonPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveOutputJsonPath(outputJsonPath, options.OutputNameHint);
        EnsureAllowedOutputPath(fullPath, _runtimeOptions.AllowedOutputRoots);
        var result = await ConvertAsync(source, options, cancellationToken);
        result.Metadata["OutputJsonPath"] = fullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(result, JsonOptions), cancellationToken);
        return result;
    }

    private string CreateWorkDirectory()
    {
        var root = string.IsNullOrWhiteSpace(_runtimeOptions.TempRoot)
            ? Path.Combine(Path.GetTempPath(), "datasync-ocr")
            : _runtimeOptions.TempRoot;

        var path = Path.Combine(root, DateTime.Now.ToString("yyyyMMddHHmmssfff"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // 临时目录清理失败不影响主流程。
        }
    }

    private static string ResolveOutputJsonPath(string configuredPath, string? outputNameHint)
    {
        var fullPath = Path.GetFullPath(configuredPath);
        var suffix = BuildOutputSuffix(outputNameHint);
        if (string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            return Path.Combine(directory, $"{fileName}_{suffix}.json");
        }

        return Path.Combine(fullPath, $"{suffix}.ocr.json");
    }

    private static string BuildOutputSuffix(string? outputNameHint)
    {
        var hint = SanitizeFileName(outputNameHint);
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var unique = Guid.NewGuid().ToString("N")[..8];
        return string.IsNullOrWhiteSpace(hint)
            ? $"ocr_{timestamp}_{unique}"
            : $"{hint}_{timestamp}_{unique}";
    }

    private static void EnsureAllowedOutputPath(string fullPath, string? allowedOutputRoots)
    {
        var allowedRoots = SplitRoots(allowedOutputRoots);
        if (allowedRoots.Count == 0)
            throw new InvalidOperationException("OCR JSON 输出未配置允许目录。");

        var resolvedPath = ResolvePathForWrite(fullPath);
        var allowed = allowedRoots
            .Select(root => Path.GetFullPath(root))
            .Any(root => IsPathUnderRoot(resolvedPath, root));

        if (!allowed)
            throw new InvalidOperationException("OCR JSON 输出路径不在允许目录内。");
    }

    private static IReadOnlyList<string> SplitRoots(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsPathUnderRoot(string fullPath, string root)
    {
        var resolvedRoot = Directory.Exists(root) || File.Exists(root)
            ? ResolvePathForWrite(root)
            : Path.GetFullPath(root);
        var relativePath = Path.GetRelativePath(resolvedRoot, fullPath);
        return relativePath == "."
            || (!relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                && relativePath != ".."
                && !Path.IsPathRooted(relativePath));
    }

    private static string ResolvePathForWrite(string path)
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
            if (Directory.Exists(current) || File.Exists(current))
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

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        return new string(chars);
    }
}
