using Microsoft.Extensions.Options;

namespace DataSync.Common.Ocr.Internal;

public sealed class PdfToPpmRenderer
{
    private const int MaxRecognitionImageEdge = 3500;

    private readonly OcrRuntimeOptions _runtimeOptions;

    public PdfToPpmRenderer(IOptions<OcrRuntimeOptions> runtimeOptions)
    {
        _runtimeOptions = runtimeOptions.Value;
    }

    public async Task<IReadOnlyList<string>> RenderAsync(
        string pdfPath,
        string workDirectory,
        OcrConversionOptions options,
        CancellationToken cancellationToken)
    {
        var files = (await RenderAsync(
            pdfPath,
            workDirectory,
            options,
            "page",
            Math.Max(72, options.Dpi),
            GetFirstPage(options),
            GetLastPage(options, false),
            cancellationToken)).ToList();

        for (var index = 0; index < files.Count; index++)
        {
            var (width, height) = await PngImageHelper.ReadSizeAsync(files[index], cancellationToken);
            if (Math.Max(width, height) <= MaxRecognitionImageEdge)
                continue;

            var scaledFile = await RenderScaledPageAsync(
                pdfPath,
                workDirectory,
                index + 1,
                options.TimeoutSeconds,
                cancellationToken);
            File.Delete(files[index]);
            files[index] = scaledFile;

        }

        return files;
    }

    public async Task<IReadOnlyList<string>> RenderPreviewAsync(
        string pdfPath,
        string workDirectory,
        OcrConversionOptions options,
        CancellationToken cancellationToken)
        => await RenderAsync(
            pdfPath,
            workDirectory,
            options,
            "preview",
            110,
            GetFirstPage(options),
            GetLastPage(options, options.ProbeNextPage),
            cancellationToken);

    public async Task<IReadOnlyList<string>> RenderPreviewPagesAsync(
        string pdfPath,
        string workDirectory,
        OcrConversionOptions options,
        int firstPage,
        int lastPage,
        CancellationToken cancellationToken)
        => await RenderAsync(
            pdfPath,
            workDirectory,
            options,
            $"preview-{firstPage:000}-{lastPage:000}",
            110,
            firstPage,
            lastPage,
            cancellationToken);

    private async Task<IReadOnlyList<string>> RenderAsync(
        string pdfPath,
        string workDirectory,
        OcrConversionOptions options,
        string outputName,
        int dpi,
        int? firstPage,
        int? lastPage,
        CancellationToken cancellationToken)
    {
        var outputPrefix = Path.Combine(workDirectory, outputName);
        var args = new List<string>
        {
            "-r",
            dpi.ToString(),
            "-png",
            "-f",
            (firstPage ?? 1).ToString()
        };
        var configuredLastPage = options.MaxPages is > 0 ? options.MaxPages : null;
        var effectiveLastPage = lastPage.HasValue && configuredLastPage.HasValue
            ? Math.Min(lastPage.Value, configuredLastPage.Value)
            : lastPage ?? configuredLastPage;
        if (effectiveLastPage.HasValue)
        {
            args.Add("-l");
            args.Add(effectiveLastPage.Value.ToString());
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

        var files = Directory.GetFiles(workDirectory, $"{outputName}-*.png")
            .OrderBy(GetPageSortKey)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException("PDF 渲染未生成任何图片。");

        return files;
    }

    private async Task<string> RenderScaledPageAsync(
        string pdfPath,
        string workDirectory,
        int pageNumber,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var outputName = $"page-scaled-{pageNumber:000}";
        var outputPrefix = Path.Combine(workDirectory, outputName);
        var args = new[]
        {
            "-scale-to",
            MaxRecognitionImageEdge.ToString(),
            "-png",
            "-f",
            pageNumber.ToString(),
            "-l",
            pageNumber.ToString(),
            pdfPath,
            outputPrefix
        };

        var result = await ProcessRunner.RunAsync(
            _runtimeOptions.PdfToPpmExecutable,
            args,
            timeoutSeconds,
            cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"PDF 第 {pageNumber} 页缩放失败：{TrimProcessError(result)}");

        return Directory.GetFiles(workDirectory, $"{outputName}-*.png").SingleOrDefault()
            ?? throw new InvalidOperationException($"PDF 第 {pageNumber} 页缩放后未生成图片。");
    }

    private static int GetPageSortKey(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var numberPart = fileName.Split('-').LastOrDefault();
        return int.TryParse(numberPart, out var number) ? number : int.MaxValue;
    }

    private static int GetFirstPage(OcrConversionOptions options)
        => Math.Max(1, options.PageRangeStart);

    private static int? GetLastPage(OcrConversionOptions options, bool includeProbePage)
    {
        var maxPage = options.MaxPages is > 0 ? options.MaxPages : null;
        if (options.PageRangeCount is not > 0)
            return maxPage;

        var lastPage = GetFirstPage(options) + options.PageRangeCount.Value - 1;
        if (includeProbePage)
            lastPage++;
        return maxPage.HasValue ? Math.Min(lastPage, maxPage.Value) : lastPage;
    }

    private static string TrimProcessError(ProcessRunResult result)
    {
        var value = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return value.Trim();
    }
}
