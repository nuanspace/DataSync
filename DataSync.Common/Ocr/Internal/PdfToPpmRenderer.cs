using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataSync.Common.Ocr.Internal;

public sealed class PdfToPpmRenderer
{
    private readonly OcrRuntimeOptions _runtimeOptions;
    private readonly ILogger<PdfToPpmRenderer> _logger;

    public PdfToPpmRenderer(IOptions<OcrRuntimeOptions> runtimeOptions, ILogger<PdfToPpmRenderer> logger)
    {
        _runtimeOptions = runtimeOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> RenderAsync(
        string pdfPath,
        string workDirectory,
        OcrConversionOptions options,
        CancellationToken cancellationToken)
    {
        var outputPrefix = Path.Combine(workDirectory, "page");
        var args = new List<string>
        {
            "-r",
            Math.Max(72, options.Dpi).ToString(),
            "-png",
            "-f",
            "1"
        };
        if (options.MaxPages.HasValue && options.MaxPages.Value > 0)
        {
            args.Add("-l");
            args.Add(options.MaxPages.Value.ToString());
        }

        args.Add(pdfPath);
        args.Add(outputPrefix);

        var result = await ProcessRunner.RunAsync(
            _runtimeOptions.PdfToPpmExecutable,
            args,
            options.TimeoutSeconds,
            cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"PDF 渲染失败：{TrimProcessError(result)}");

        var files = Directory.GetFiles(workDirectory, "page-*.png")
            .OrderBy(GetPageSortKey)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException("PDF 渲染未生成任何图片。");

        _logger.LogInformation("PDF 已渲染为 {Count} 张图片", files.Count);
        return files;
    }

    private static int GetPageSortKey(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var numberPart = fileName.Split('-').LastOrDefault();
        return int.TryParse(numberPart, out var number) ? number : int.MaxValue;
    }

    private static string TrimProcessError(ProcessRunResult result)
    {
        var value = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return value.Trim();
    }
}
