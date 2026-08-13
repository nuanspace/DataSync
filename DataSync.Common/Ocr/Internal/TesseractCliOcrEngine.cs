using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DataSync.Common.Ocr.Internal;

public sealed class TesseractCliOcrEngine
{
    private readonly OcrRuntimeOptions _runtimeOptions;

    public TesseractCliOcrEngine(IOptions<OcrRuntimeOptions> runtimeOptions)
    {
        _runtimeOptions = runtimeOptions.Value;
    }

    public async Task<OcrPageResult> ReadAsync(
        string imagePath,
        int pageNumber,
        OcrConversionOptions options,
        CancellationToken cancellationToken,
        int? pageSegMode = null,
        string? outputSuffix = null)
    {
        var outputBase = Path.Combine(
            Path.GetDirectoryName(imagePath) ?? "",
            $"ocr-{pageNumber:000}{outputSuffix}");
        var args = new[]
        {
            imagePath,
            outputBase,
            "-l",
            options.Language,
            "--psm",
            Math.Max(0, pageSegMode ?? options.PageSegMode).ToString(CultureInfo.InvariantCulture),
            "-c",
            "preserve_interword_spaces=1",
            "-c",
            "textord_tabfind_find_tables=0",
            "-c",
            "tessedit_create_txt=1",
            "-c",
            "tessedit_create_tsv=1"
        };

        var result = await ProcessRunner.RunAsync(
            _runtimeOptions.TesseractExecutable,
            args,
            options.TimeoutSeconds,
            cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Tesseract OCR 失败：{TrimProcessError(result)}");

        if (HasLanguageLoadFailure(result))
            throw new InvalidOperationException("OCR 语言组件未安装完整，请安装中文识别组件后重试。");

        var textPath = outputBase + ".txt";
        var tsvPath = outputBase + ".tsv";
        var text = File.Exists(textPath) ? NormalizeOcrText(await File.ReadAllTextAsync(textPath, cancellationToken)) : "";
        var items = File.Exists(tsvPath) ? await ParseTsvAsync(tsvPath, pageNumber, cancellationToken) : [];
        var confidence = items.Count == 0 ? 0 : Math.Round(items.Average(item => item.Confidence), 4);

        return new OcrPageResult
        {
            PageNumber = pageNumber,
            Text = text,
            Lines = SplitLines(text),
            TextItems = items,
            MeanConfidence = confidence,
            RenderedImagePath = options.KeepWorkFiles ? imagePath : null
        };
    }

    private static async Task<IReadOnlyList<OcrTextItem>> ParseTsvAsync(
        string path,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var items = new List<OcrTextItem>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 12)
                continue;

            var text = parts[11].Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (!double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) || conf < 0)
                continue;

            items.Add(new OcrTextItem
            {
                PageNumber = pageNumber,
                Text = text,
                X = ParseInt(parts[6]),
                Y = ParseInt(parts[7]),
                Width = ParseInt(parts[8]),
                Height = ParseInt(parts[9]),
                Confidence = Math.Round(conf / 100d, 4)
            });
        }

        return items;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string NormalizeOcrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.Trim();
    }

    private static IReadOnlyList<string> SplitLines(string text)
        => text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    private static string TrimProcessError(ProcessRunResult result)
    {
        var value = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return value.Trim();
    }

    private static bool HasLanguageLoadFailure(ProcessRunResult result)
        => result.Error.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("Error opening data file", StringComparison.OrdinalIgnoreCase);
}
