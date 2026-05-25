using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 接入项目文档服务
/// </summary>
public class ProjectDocumentService
{
    public const string CategoryInterface = "接口文档";
    public const string CategoryGuide = "对接说明";
    public const string CategorySample = "样例报文";
    public const string CategoryOther = "其他";
    public const string RequestPathPrefix = "/project-documents";
    public const string BrowserPreviewPathPrefix = "/project-documents/view";

    private static readonly XNamespace WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace OfficeDocumentRelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WordprocessingDrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly HashSet<string> PreviewableExtensions =
    [
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".txt", ".json", ".xml", ".csv", ".log", ".md", ".htm", ".html", ".docx"
    ];

    private static readonly HashSet<string> TextPreviewableExtensions =
    [
        ".txt", ".json", ".xml", ".csv", ".log", ".md", ".htm", ".html", ".docx"
    ];

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IWebHostEnvironment _environment;

    public ProjectDocumentService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IWebHostEnvironment environment)
    {
        _contextFactory = contextFactory;
        _environment = environment;
    }

    public static IReadOnlyList<string> Categories =>
    [
        CategoryInterface,
        CategoryGuide,
        CategorySample,
        CategoryOther
    ];

    public string RootPath
    {
        get
        {
            var path = Path.Combine(_environment.ContentRootPath, "ProjectDocuments");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public async Task<List<EsbIntegrationProjectDocument>> GetDocumentsAsync(
        string integrationProjectCode,
        string? category = null,
        bool includeDeleted = false)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var query = db.EsbIntegrationProjectDocuments
            .AsNoTracking()
            .Where(d => d.IntegrationProjectCode == integrationProjectCode);

        if (!includeDeleted)
        {
            query = query.Where(d => !d.IsDeleted);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(d => d.Category == category);
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .ToListAsync();
    }

    public async Task<EsbIntegrationProjectDocument?> GetDocumentAsync(int id)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        return await db.EsbIntegrationProjectDocuments.FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<EsbIntegrationProjectDocument> UploadAsync(
        string integrationProjectCode,
        string category,
        IBrowserFile file,
        string? title,
        string? remark)
    {
        var normalizedProjectCode = NormalizeProjectCode(integrationProjectCode);
        var normalizedCategory = NormalizeCategory(category);
        var normalizedTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(file.Name)
            : title.Trim();
        var storedFileName = BuildStoredFileName(file.Name);
        var relativePath = $"{normalizedProjectCode}/{storedFileName}";
        var physicalPath = GetPhysicalPath(relativePath);
        var directoryPath = Path.GetDirectoryName(physicalPath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await using (var targetStream = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var sourceStream = file.OpenReadStream(long.MaxValue))
        {
            await sourceStream.CopyToAsync(targetStream);
        }

        var now = DateTime.Now;
        var document = new EsbIntegrationProjectDocument
        {
            IntegrationProjectCode = normalizedProjectCode,
            Category = normalizedCategory,
            Title = normalizedTitle,
            OriginalFileName = file.Name,
            StoredFileName = storedFileName,
            StoredRelativePath = relativePath,
            ContentType = file.ContentType,
            FileSize = file.Size,
            Remark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(),
            IsDeleted = false,
            SortOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var db = await _contextFactory.CreateDbContextAsync();
        db.EsbIntegrationProjectDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    public async Task UpdateMetadataAsync(int id, string category, string title, string? remark)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var entity = await db.EsbIntegrationProjectDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (entity == null)
        {
            throw new InvalidOperationException("文档不存在");
        }

        entity.Category = NormalizeCategory(category);
        entity.Title = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(entity.OriginalFileName)
            : title.Trim();
        entity.Remark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        entity.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync();
    }

    public async Task SoftDeleteAsync(int id)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var entity = await db.EsbIntegrationProjectDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (entity == null || entity.IsDeleted)
        {
            return;
        }

        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
    }

    public string GetPhysicalPath(EsbIntegrationProjectDocument document)
        => GetPhysicalPath(document.StoredRelativePath);

    public string GetDirectoryPath(EsbIntegrationProjectDocument document)
        => Path.GetDirectoryName(GetPhysicalPath(document)) ?? RootPath;

    public string GetPreviewUrl(EsbIntegrationProjectDocument document)
    {
        var encodedSegments = document.StoredRelativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return $"{RequestPathPrefix}/{string.Join("/", encodedSegments)}";
    }

    public string GetBrowserPreviewUrl(EsbIntegrationProjectDocument document)
        => $"{BrowserPreviewPathPrefix}/{document.Id}";

    public bool CanPreview(EsbIntegrationProjectDocument document)
        => PreviewableExtensions.Contains(GetExtension(document.OriginalFileName));

    public bool IsTextPreview(EsbIntegrationProjectDocument document)
        => TextPreviewableExtensions.Contains(GetExtension(document.OriginalFileName));

    public bool IsWordDocument(EsbIntegrationProjectDocument document)
        => string.Equals(GetExtension(document.OriginalFileName), ".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> ReadTextPreviewAsync(EsbIntegrationProjectDocument document, int maxChars = 200_000)
    {
        if (!IsTextPreview(document))
        {
            return null;
        }

        var path = GetPhysicalPath(document);
        if (!File.Exists(path))
        {
            return "文件不存在。";
        }

        var extension = GetExtension(document.OriginalFileName);
        if (extension == ".docx")
        {
            return ReadDocxTextPreview(path, maxChars);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars];
        var readCount = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, readCount);
        if (!reader.EndOfStream)
        {
            text += Environment.NewLine + Environment.NewLine + "......（内容过长，已截断预览）";
        }

        return text;
    }

    public string BuildBrowserPreviewHtml(EsbIntegrationProjectDocument document)
    {
        if (!IsWordDocument(document))
        {
            throw new InvalidOperationException("仅支持 Word 文档生成浏览器预览页");
        }

        var path = GetPhysicalPath(document);
        if (!File.Exists(path))
        {
            return BuildFallbackPreviewHtml(document, "文档文件不存在。");
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var documentXml = LoadXmlEntry(archive, "word/document.xml");
            if (documentXml?.Root == null)
            {
                return BuildFallbackPreviewHtml(document, "未找到 Word 正文内容。");
            }

            var styles = LoadWordStyles(archive);
            var relationships = LoadWordRelationships(archive);
            var context = new WordRenderContext(archive, styles, relationships);
            var body = documentXml.Root.Element(WordNamespace + "body");

            var bodyHtmlBuilder = new StringBuilder();
            if (body != null)
            {
                foreach (var element in body.Elements())
                {
                    if (element.Name == WordNamespace + "sectPr")
                    {
                        continue;
                    }

                    bodyHtmlBuilder.Append(RenderBlockElement(element, context));
                }
            }

            var bodyHtml = bodyHtmlBuilder.Length == 0
                ? "<p class=\"docx-paragraph\">文档没有可显示的内容。</p>"
                : bodyHtmlBuilder.ToString();

            return BuildPreviewPageHtml(document, bodyHtml);
        }
        catch (Exception ex)
        {
            return BuildFallbackPreviewHtml(document, $"浏览器预览生成失败：{WebUtility.HtmlEncode(ex.Message)}");
        }
    }

    public static string BuildFileUri(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Length >= 2 && normalized[1] == ':'
            ? $"file:///{normalized}"
            : $"file://{normalized}";
    }

    private string GetPhysicalPath(string relativePath)
    {
        var segments = relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Aggregate(RootPath, Path.Combine);
    }

    private static string NormalizeProjectCode(string integrationProjectCode)
        => string.IsNullOrWhiteSpace(integrationProjectCode)
            ? throw new InvalidOperationException("接入项目编码不能为空")
            : integrationProjectCode.Trim().ToUpperInvariant();

    private static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return CategoryOther;
        }

        var normalized = category.Trim();
        return Categories.Contains(normalized) ? normalized : CategoryOther;
    }

    private static string BuildStoredFileName(string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        var safeBaseName = SanitizeFileName(baseName);
        var suffix = $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..24];
        return $"{safeBaseName}-{suffix}{extension}";
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        var normalized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "document" : normalized;
    }

    private static string GetExtension(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant();

    private string BuildPreviewPageHtml(EsbIntegrationProjectDocument document, string bodyHtml)
    {
        var title = WebUtility.HtmlEncode(document.Title);
        var originalFileName = WebUtility.HtmlEncode(document.OriginalFileName);
        var category = WebUtility.HtmlEncode(document.Category);
        var projectCode = WebUtility.HtmlEncode(document.IntegrationProjectCode);
        var sourceUrl = WebUtility.HtmlEncode(GetPreviewUrl(document));

        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{{title}}</title>
            <style>
                :root {
                    color-scheme: light;
                    --bg: #eef3f7;
                    --card: #ffffff;
                    --line: #d8e2ea;
                    --text: #152534;
                    --muted: #5b7185;
                    --accent: #0f6b7d;
                    --accent-dark: #0c5562;
                }

                * { box-sizing: border-box; }
                body { margin: 0; background: linear-gradient(180deg, #f6fafc 0%, var(--bg) 100%); color: var(--text); font-family: "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", sans-serif; }
                .preview-shell { min-height: 100vh; }
                .preview-toolbar { position: sticky; top: 0; z-index: 10; display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; padding: 18px 24px; border-bottom: 1px solid rgba(216, 226, 234, 0.9); background: rgba(255, 255, 255, 0.92); backdrop-filter: blur(14px); }
                .preview-title { margin: 0; font-size: 24px; line-height: 1.35; }
                .preview-meta { margin-top: 6px; color: var(--muted); font-size: 13px; }
                .preview-actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
                .preview-action { display: inline-flex; align-items: center; justify-content: center; min-height: 40px; padding: 0 16px; border-radius: 999px; border: 1px solid var(--line); background: #fff; color: var(--text); text-decoration: none; font-size: 14px; }
                .preview-action-primary { border-color: var(--accent); background: var(--accent); color: #fff; }
                .preview-action:hover { border-color: var(--accent-dark); color: var(--accent-dark); }
                .preview-action-primary:hover { background: var(--accent-dark); color: #fff; }
                .preview-main { padding: 28px 18px 40px; }
                .preview-paper-wrap { overflow-x: auto; }
                .preview-paper { width: min(1180px, 100%); margin: 0 auto; padding: 42px 48px; border-radius: 20px; border: 1px solid rgba(216, 226, 234, 0.9); background: var(--card); box-shadow: 0 18px 50px rgba(20, 44, 68, 0.08); }
                .docx-content { color: var(--text); line-height: 1.7; }
                .docx-content a { color: #0b6ca8; text-decoration: none; }
                .docx-content a:hover { text-decoration: underline; }
                .docx-paragraph, .docx-heading, .docx-table-cell { white-space: pre-wrap; word-break: break-word; }
                .docx-paragraph { margin: 0 0 12px; min-height: 1em; }
                .docx-heading { margin: 18px 0 10px; line-height: 1.45; }
                .docx-heading-1 { font-size: 30px; }
                .docx-heading-2 { font-size: 24px; }
                .docx-heading-3 { font-size: 20px; }
                .docx-heading-4 { font-size: 18px; }
                .docx-heading-5 { font-size: 16px; }
                .docx-heading-6 { font-size: 15px; }
                .docx-table-wrap { width: 100%; overflow-x: auto; margin: 18px 0; }
                .docx-table { width: 100%; border-collapse: collapse; table-layout: auto; }
                .docx-table td, .docx-table th { min-width: 80px; padding: 8px 10px; border: 1px solid #b7c8d6; vertical-align: top; }
                .docx-table td .docx-paragraph:last-child, .docx-table th .docx-paragraph:last-child { margin-bottom: 0; }
                .docx-image { display: block; max-width: 100%; height: auto; margin: 10px auto; }
                .docx-page-break { margin: 26px 0; border: 0; border-top: 1px dashed #b7c8d6; }
                .preview-empty { padding: 20px 22px; border-radius: 14px; background: #f7fafc; border: 1px solid var(--line); color: var(--muted); }
                @media (max-width: 900px) {
                    .preview-toolbar { flex-direction: column; }
                    .preview-main { padding: 18px 10px 24px; }
                    .preview-paper { padding: 24px 18px; border-radius: 14px; }
                }
            </style>
        </head>
        <body>
            <div class="preview-shell">
                <header class="preview-toolbar">
                    <div>
                        <h1 class="preview-title">{{title}}</h1>
                        <div class="preview-meta">接入项目：{{projectCode}}　|　分类：{{category}}　|　文件：{{originalFileName}}</div>
                    </div>
                    <div class="preview-actions">
                        <a class="preview-action preview-action-primary" href="{{sourceUrl}}" target="_blank" rel="noopener noreferrer">打开原文件</a>
                        <a class="preview-action" href="javascript:history.back()">返回上一页</a>
                    </div>
                </header>
                <main class="preview-main">
                    <div class="preview-paper-wrap">
                        <article class="preview-paper docx-content">
                            {{bodyHtml}}
                        </article>
                    </div>
                </main>
            </div>
        </body>
        </html>
        """;
    }

    private string BuildFallbackPreviewHtml(EsbIntegrationProjectDocument document, string message)
    {
        var content = $$"""
        <div class="preview-empty">
            {{message}}
        </div>
        """;

        return BuildPreviewPageHtml(document, content);
    }

    private static XDocument? LoadXmlEntry(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static Dictionary<string, WordStyleInfo> LoadWordStyles(ZipArchive archive)
    {
        var styles = new Dictionary<string, WordStyleInfo>(StringComparer.OrdinalIgnoreCase);
        var stylesXml = LoadXmlEntry(archive, "word/styles.xml");
        if (stylesXml?.Root == null)
        {
            return styles;
        }

        foreach (var styleElement in stylesXml.Root.Elements(WordNamespace + "style"))
        {
            var styleId = styleElement.Attribute(WordNamespace + "styleId")?.Value;
            if (string.IsNullOrWhiteSpace(styleId))
            {
                continue;
            }

            styles[styleId] = ParseStyleElement(styleElement);
        }

        return styles;
    }

    private static Dictionary<string, WordRelationshipInfo> LoadWordRelationships(ZipArchive archive)
    {
        var relationships = new Dictionary<string, WordRelationshipInfo>(StringComparer.OrdinalIgnoreCase);
        var relationshipsXml = LoadXmlEntry(archive, "word/_rels/document.xml.rels");
        if (relationshipsXml?.Root == null)
        {
            return relationships;
        }

        foreach (var element in relationshipsXml.Root.Elements(PackageRelationshipsNamespace + "Relationship"))
        {
            var id = element.Attribute("Id")?.Value;
            var type = element.Attribute("Type")?.Value;
            var target = element.Attribute("Target")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            relationships[id] = new WordRelationshipInfo
            {
                Type = type,
                Target = target,
                TargetMode = element.Attribute("TargetMode")?.Value
            };
        }

        return relationships;
    }

    private static WordStyleInfo ParseStyleElement(XElement styleElement)
    {
        var info = new WordStyleInfo
        {
            Name = GetValAttribute(styleElement.Element(WordNamespace + "name"))
        };

        var paragraphFormatting = ParseParagraphFormatting(styleElement.Element(WordNamespace + "pPr"));
        var runFormatting = ParseRunFormatting(styleElement.Element(WordNamespace + "rPr"));
        return MergeStyleInfo(MergeStyleInfo(info, paragraphFormatting), runFormatting);
    }

    private static WordStyleInfo ParseParagraphFormatting(XElement? paragraphProperties)
    {
        var info = new WordStyleInfo();
        if (paragraphProperties == null)
        {
            return info;
        }

        info.Alignment = GetValAttribute(paragraphProperties.Element(WordNamespace + "jc"));

        var spacing = paragraphProperties.Element(WordNamespace + "spacing");
        info.BeforeSpacingTwips = ParseIntAttribute(spacing, "before");
        info.AfterSpacingTwips = ParseIntAttribute(spacing, "after");

        var indentation = paragraphProperties.Element(WordNamespace + "ind");
        info.LeftIndentTwips = ParseIntAttribute(indentation, "left");
        info.RightIndentTwips = ParseIntAttribute(indentation, "right");
        info.FirstLineTwips = ParseIntAttribute(indentation, "firstLine");

        return MergeStyleInfo(info, ParseRunFormatting(paragraphProperties.Element(WordNamespace + "rPr")));
    }

    private static WordStyleInfo ParseRunFormatting(XElement? runProperties)
    {
        var info = new WordStyleInfo();
        if (runProperties == null)
        {
            return info;
        }

        var fonts = runProperties.Element(WordNamespace + "rFonts");
        info.FontFamily =
            fonts?.Attribute(WordNamespace + "eastAsia")?.Value ??
            fonts?.Attribute(WordNamespace + "ascii")?.Value ??
            fonts?.Attribute(WordNamespace + "hAnsi")?.Value;

        info.Color = GetValAttribute(runProperties.Element(WordNamespace + "color"));
        info.HighlightColor = GetValAttribute(runProperties.Element(WordNamespace + "highlight"));
        info.FontSizeHalfPoints = ParseIntAttribute(runProperties.Element(WordNamespace + "sz"), "val");
        info.Bold = HasWordToggle(runProperties.Element(WordNamespace + "b"));
        info.Italic = HasWordToggle(runProperties.Element(WordNamespace + "i"));
        info.Underline = HasWordToggle(runProperties.Element(WordNamespace + "u"));
        info.Strike = HasWordToggle(runProperties.Element(WordNamespace + "strike"));
        return info;
    }

    private static WordStyleInfo MergeStyleInfo(WordStyleInfo? baseStyle, WordStyleInfo? overrideStyle)
    {
        return new WordStyleInfo
        {
            Name = overrideStyle?.Name ?? baseStyle?.Name,
            Alignment = overrideStyle?.Alignment ?? baseStyle?.Alignment,
            BeforeSpacingTwips = overrideStyle?.BeforeSpacingTwips ?? baseStyle?.BeforeSpacingTwips,
            AfterSpacingTwips = overrideStyle?.AfterSpacingTwips ?? baseStyle?.AfterSpacingTwips,
            LeftIndentTwips = overrideStyle?.LeftIndentTwips ?? baseStyle?.LeftIndentTwips,
            RightIndentTwips = overrideStyle?.RightIndentTwips ?? baseStyle?.RightIndentTwips,
            FirstLineTwips = overrideStyle?.FirstLineTwips ?? baseStyle?.FirstLineTwips,
            FontFamily = overrideStyle?.FontFamily ?? baseStyle?.FontFamily,
            Color = overrideStyle?.Color ?? baseStyle?.Color,
            HighlightColor = overrideStyle?.HighlightColor ?? baseStyle?.HighlightColor,
            FontSizeHalfPoints = overrideStyle?.FontSizeHalfPoints ?? baseStyle?.FontSizeHalfPoints,
            Bold = overrideStyle?.Bold ?? baseStyle?.Bold,
            Italic = overrideStyle?.Italic ?? baseStyle?.Italic,
            Underline = overrideStyle?.Underline ?? baseStyle?.Underline,
            Strike = overrideStyle?.Strike ?? baseStyle?.Strike
        };
    }

    private static string RenderBlockElement(XElement element, WordRenderContext context)
    {
        if (element.Name == WordNamespace + "p")
        {
            return RenderParagraph(element, context);
        }

        if (element.Name == WordNamespace + "tbl")
        {
            return RenderTable(element, context);
        }

        return string.Empty;
    }

    private static string RenderParagraph(XElement paragraph, WordRenderContext context)
    {
        var paragraphStyle = GetParagraphStyleInfo(paragraph, context);
        var tagName = GetParagraphTag(paragraphStyle.Name);
        var inlineContent = RenderInlineChildren(paragraph.Elements(), context, paragraphStyle);
        if (string.IsNullOrWhiteSpace(inlineContent))
        {
            inlineContent = "&nbsp;";
        }

        var css = BuildParagraphCss(paragraphStyle);
        var className = tagName == "p" ? "docx-paragraph" : $"docx-heading docx-heading-{tagName[1]}";
        var styleAttribute = string.IsNullOrWhiteSpace(css) ? string.Empty : $" style=\"{WebUtility.HtmlEncode(css)}\"";
        return $"<{tagName} class=\"{className}\"{styleAttribute}>{inlineContent}</{tagName}>";
    }

    private static string RenderTable(XElement table, WordRenderContext context)
    {
        var builder = new StringBuilder();
        builder.Append("<div class=\"docx-table-wrap\"><table class=\"docx-table\">");

        foreach (var row in table.Elements(WordNamespace + "tr"))
        {
            builder.Append("<tr>");
            foreach (var cell in row.Elements(WordNamespace + "tc"))
            {
                builder.Append(RenderTableCell(cell, context));
            }
            builder.Append("</tr>");
        }

        builder.Append("</table></div>");
        return builder.ToString();
    }

    private static string RenderTableCell(XElement cell, WordRenderContext context)
    {
        var cellProperties = cell.Element(WordNamespace + "tcPr");
        var colspan = ParseIntAttribute(cellProperties?.Element(WordNamespace + "gridSpan"), "val");
        var shading = GetValAttribute(cellProperties?.Element(WordNamespace + "shd"), "fill");

        var attributes = new List<string>();
        var colspanValue = colspan.GetValueOrDefault();
        if (colspanValue > 1)
        {
            attributes.Add($"colspan=\"{colspanValue}\"");
        }

        var styles = new List<string>();
        var background = NormalizeCssColor(shading);
        if (!string.IsNullOrWhiteSpace(background))
        {
            styles.Add($"background:{background}");
        }

        if (styles.Count > 0)
        {
            attributes.Add($"style=\"{WebUtility.HtmlEncode(string.Join(";", styles))}\"");
        }

        var contentBuilder = new StringBuilder();
        foreach (var child in cell.Elements())
        {
            if (child.Name == WordNamespace + "tcPr")
            {
                continue;
            }

            contentBuilder.Append(RenderBlockElement(child, context));
        }

        if (contentBuilder.Length == 0)
        {
            contentBuilder.Append("<p class=\"docx-paragraph\">&nbsp;</p>");
        }

        var attributeText = attributes.Count == 0 ? string.Empty : " " + string.Join(" ", attributes);
        return $"<td class=\"docx-table-cell\"{attributeText}>{contentBuilder}</td>";
    }

    private static string RenderInlineChildren(IEnumerable<XElement> elements, WordRenderContext context, WordStyleInfo paragraphStyle)
    {
        var builder = new StringBuilder();
        foreach (var element in elements)
        {
            builder.Append(RenderInlineElement(element, context, paragraphStyle));
        }

        return builder.ToString();
    }

    private static string RenderInlineElement(XElement element, WordRenderContext context, WordStyleInfo paragraphStyle)
    {
        if (element.Name == WordNamespace + "r")
        {
            return RenderRun(element, context, paragraphStyle);
        }

        if (element.Name == WordNamespace + "hyperlink")
        {
            return RenderHyperlink(element, context, paragraphStyle);
        }

        if (element.Name == WordNamespace + "ins"
            || element.Name == WordNamespace + "sdt"
            || element.Name == WordNamespace + "smartTag")
        {
            return RenderInlineChildren(element.Elements(), context, paragraphStyle);
        }

        return string.Empty;
    }

    private static string RenderHyperlink(XElement hyperlink, WordRenderContext context, WordStyleInfo paragraphStyle)
    {
        var content = RenderInlineChildren(hyperlink.Elements(), context, paragraphStyle);
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var relationshipId = hyperlink.Attribute(OfficeDocumentRelationshipsNamespace + "id")?.Value;
        var href = context.GetHyperlinkTarget(relationshipId);
        if (string.IsNullOrWhiteSpace(href))
        {
            return content;
        }

        return $"<a href=\"{WebUtility.HtmlEncode(href)}\" target=\"_blank\" rel=\"noopener noreferrer\">{content}</a>";
    }

    private static string RenderRun(XElement run, WordRenderContext context, WordStyleInfo paragraphStyle)
    {
        var runStyle = GetRunStyleInfo(run, paragraphStyle, context);
        var contentBuilder = new StringBuilder();

        foreach (var child in run.Elements())
        {
            if (child.Name == WordNamespace + "t")
            {
                contentBuilder.Append(WebUtility.HtmlEncode(child.Value));
            }
            else if (child.Name == WordNamespace + "tab")
            {
                contentBuilder.Append("&emsp;");
            }
            else if (child.Name == WordNamespace + "br" || child.Name == WordNamespace + "cr")
            {
                var breakType = GetValAttribute(child);
                contentBuilder.Append(string.Equals(breakType, "page", StringComparison.OrdinalIgnoreCase)
                    ? "<hr class=\"docx-page-break\" />"
                    : "<br />");
            }
            else if (child.Name == WordNamespace + "drawing")
            {
                contentBuilder.Append(RenderDrawing(child, context));
            }
            else if (child.Name == WordNamespace + "noBreakHyphen")
            {
                contentBuilder.Append("-");
            }
            else if (child.Name == WordNamespace + "sym")
            {
                var symbol = GetValAttribute(child, "char");
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    contentBuilder.Append($"&#x{WebUtility.HtmlEncode(symbol)};");
                }
            }
        }

        var content = contentBuilder.ToString();
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var css = BuildRunCss(runStyle);
        if (string.IsNullOrWhiteSpace(css))
        {
            return content;
        }

        return $"<span style=\"{WebUtility.HtmlEncode(css)}\">{content}</span>";
    }

    private static string RenderDrawing(XElement drawing, WordRenderContext context)
    {
        var blip = drawing.Descendants(DrawingNamespace + "blip").FirstOrDefault();
        var relationshipId = blip?.Attribute(OfficeDocumentRelationshipsNamespace + "embed")?.Value;
        var dataUri = context.GetImageDataUri(relationshipId);
        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return string.Empty;
        }

        var sizeCss = new List<string> { "max-width:100%", "height:auto" };
        var extent = drawing.Descendants(WordprocessingDrawingNamespace + "extent").FirstOrDefault();
        if (extent != null)
        {
            var widthPx = ConvertEmuToPixels(extent.Attribute("cx")?.Value);
            if (widthPx > 0)
            {
                sizeCss.Add($"width:{widthPx.ToString("0.##", CultureInfo.InvariantCulture)}px");
            }
        }

        return $"<img class=\"docx-image\" src=\"{dataUri}\" style=\"{WebUtility.HtmlEncode(string.Join(";", sizeCss))}\" alt=\"文档图片\" />";
    }

    private static WordStyleInfo GetParagraphStyleInfo(XElement paragraph, WordRenderContext context)
    {
        var style = new WordStyleInfo();
        var paragraphProperties = paragraph.Element(WordNamespace + "pPr");
        var styleId = GetValAttribute(paragraphProperties?.Element(WordNamespace + "pStyle"));
        if (!string.IsNullOrWhiteSpace(styleId) && context.Styles.TryGetValue(styleId, out var styleInfo))
        {
            style = MergeStyleInfo(style, styleInfo);
        }

        return MergeStyleInfo(style, ParseParagraphFormatting(paragraphProperties));
    }

    private static WordStyleInfo GetRunStyleInfo(XElement run, WordStyleInfo paragraphStyle, WordRenderContext context)
    {
        var style = MergeStyleInfo(new WordStyleInfo(), paragraphStyle);
        var runProperties = run.Element(WordNamespace + "rPr");
        var styleId = GetValAttribute(runProperties?.Element(WordNamespace + "rStyle"));
        if (!string.IsNullOrWhiteSpace(styleId) && context.Styles.TryGetValue(styleId, out var styleInfo))
        {
            style = MergeStyleInfo(style, styleInfo);
        }

        return MergeStyleInfo(style, ParseRunFormatting(runProperties));
    }

    private static string GetParagraphTag(string? styleName)
    {
        var level = GetHeadingLevel(styleName);
        if (level.HasValue)
        {
            return $"h{level.Value}";
        }

        if (string.Equals(styleName, "title", StringComparison.OrdinalIgnoreCase))
        {
            return "h1";
        }

        if (string.Equals(styleName, "subtitle", StringComparison.OrdinalIgnoreCase))
        {
            return "h2";
        }

        return "p";
    }

    private static int? GetHeadingLevel(string? styleName)
    {
        if (string.IsNullOrWhiteSpace(styleName))
        {
            return null;
        }

        var normalized = styleName
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        if (!normalized.StartsWith("heading", StringComparison.Ordinal) || normalized.Length <= "heading".Length)
        {
            return null;
        }

        return int.TryParse(normalized["heading".Length..], out var level) && level is >= 1 and <= 6
            ? level
            : null;
    }

    private static string BuildParagraphCss(WordStyleInfo style)
    {
        var styles = new List<string>();
        var alignment = MapAlignment(style.Alignment);
        if (!string.IsNullOrWhiteSpace(alignment))
        {
            styles.Add($"text-align:{alignment}");
        }

        var beforeSpacing = style.BeforeSpacingTwips.GetValueOrDefault();
        if (beforeSpacing > 0)
        {
            styles.Add($"margin-top:{ToPointText(beforeSpacing / 20d)}");
        }

        var afterSpacing = style.AfterSpacingTwips.GetValueOrDefault();
        if (afterSpacing > 0)
        {
            styles.Add($"margin-bottom:{ToPointText(afterSpacing / 20d)}");
        }

        var leftIndent = style.LeftIndentTwips.GetValueOrDefault();
        if (leftIndent > 0)
        {
            styles.Add($"padding-left:{ToPointText(leftIndent / 20d)}");
        }

        var rightIndent = style.RightIndentTwips.GetValueOrDefault();
        if (rightIndent > 0)
        {
            styles.Add($"padding-right:{ToPointText(rightIndent / 20d)}");
        }

        var firstLineIndent = style.FirstLineTwips.GetValueOrDefault();
        if (firstLineIndent > 0)
        {
            styles.Add($"text-indent:{ToPointText(firstLineIndent / 20d)}");
        }

        var fontFamily = NormalizeFontFamily(style.FontFamily);
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            styles.Add($"font-family:{fontFamily}");
        }

        var color = NormalizeCssColor(style.Color);
        if (!string.IsNullOrWhiteSpace(color))
        {
            styles.Add($"color:{color}");
        }

        var paragraphFontSize = style.FontSizeHalfPoints.GetValueOrDefault();
        if (paragraphFontSize > 0)
        {
            styles.Add($"font-size:{ToPointText(paragraphFontSize / 2d)}");
        }

        if (style.Bold == true)
        {
            styles.Add("font-weight:700");
        }

        if (style.Italic == true)
        {
            styles.Add("font-style:italic");
        }

        if (style.Underline == true || style.Strike == true)
        {
            var decorations = new List<string>();
            if (style.Underline == true)
            {
                decorations.Add("underline");
            }

            if (style.Strike == true)
            {
                decorations.Add("line-through");
            }

            styles.Add($"text-decoration:{string.Join(' ', decorations)}");
        }

        var highlight = NormalizeCssColor(style.HighlightColor);
        if (!string.IsNullOrWhiteSpace(highlight))
        {
            styles.Add($"background:{highlight}");
        }

        return string.Join(";", styles);
    }

    private static string BuildRunCss(WordStyleInfo style)
    {
        var styles = new List<string>();
        var fontFamily = NormalizeFontFamily(style.FontFamily);
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            styles.Add($"font-family:{fontFamily}");
        }

        var color = NormalizeCssColor(style.Color);
        if (!string.IsNullOrWhiteSpace(color))
        {
            styles.Add($"color:{color}");
        }

        var runFontSize = style.FontSizeHalfPoints.GetValueOrDefault();
        if (runFontSize > 0)
        {
            styles.Add($"font-size:{ToPointText(runFontSize / 2d)}");
        }

        if (style.Bold == true)
        {
            styles.Add("font-weight:700");
        }

        if (style.Italic == true)
        {
            styles.Add("font-style:italic");
        }

        if (style.Underline == true || style.Strike == true)
        {
            var decorations = new List<string>();
            if (style.Underline == true)
            {
                decorations.Add("underline");
            }

            if (style.Strike == true)
            {
                decorations.Add("line-through");
            }

            styles.Add($"text-decoration:{string.Join(' ', decorations)}");
        }

        var highlight = NormalizeCssColor(style.HighlightColor);
        if (!string.IsNullOrWhiteSpace(highlight))
        {
            styles.Add($"background:{highlight}");
        }

        return string.Join(";", styles);
    }

    private static string? NormalizeFontFamily(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
        {
            return null;
        }

        return $"\"{fontFamily.Trim().Replace("\"", string.Empty, StringComparison.Ordinal)}\"";
    }

    private static string? NormalizeCssColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)
            || string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(color, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = color.Trim();
        if (normalized.Length == 6 && normalized.All(Uri.IsHexDigit))
        {
            return $"#{normalized}";
        }

        return normalized.ToLowerInvariant() switch
        {
            "yellow" => "#fff59d",
            "green" => "#c8e6c9",
            "cyan" => "#b2ebf2",
            "magenta" => "#f8bbd0",
            "blue" => "#bbdefb",
            "red" => "#ffcdd2",
            "darkblue" => "#90caf9",
            "darkcyan" => "#80deea",
            "darkgreen" => "#a5d6a7",
            "darkmagenta" => "#ce93d8",
            "darkred" => "#ef9a9a",
            "darkyellow" => "#ffe082",
            "lightgray" => "#eceff1",
            "darkgray" => "#cfd8dc",
            "black" => "#000000",
            "white" => "#ffffff",
            _ => null
        };
    }

    private static string? MapAlignment(string? alignment)
    {
        return alignment?.ToLowerInvariant() switch
        {
            "center" => "center",
            "right" => "right",
            "both" => "justify",
            "distribute" => "justify",
            _ => null
        };
    }

    private static bool? HasWordToggle(XElement? element)
    {
        if (element == null)
        {
            return null;
        }

        var value = GetValAttribute(element);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetValAttribute(XElement? element, string attributeName = "val")
        => element?.Attribute(WordNamespace + attributeName)?.Value ?? element?.Attribute(attributeName)?.Value;

    private static int? ParseIntAttribute(XElement? element, string attributeName)
    {
        var value = GetValAttribute(element, attributeName);
        return int.TryParse(value, out var number) ? number : null;
    }

    private static double ConvertEmuToPixels(string? value)
    {
        if (!long.TryParse(value, out var emu) || emu <= 0)
        {
            return 0;
        }

        return emu / 9525d;
    }

    private static string ToPointText(double points)
        => $"{points.ToString("0.##", CultureInfo.InvariantCulture)}pt";

    private static string ReadDocxTextPreview(string path, int maxChars)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry == null)
        {
            return "未找到 Word 正文内容。";
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        if (document.Root == null)
        {
            return "Word 文档内容为空。";
        }

        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        var truncated = false;

        foreach (var paragraph in document.Descendants(WordNamespace + "p"))
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == WordNamespace + "t")
                {
                    truncated = AppendPreviewText(builder, node.Value, maxChars) || truncated;
                }
                else if (node.Name == WordNamespace + "tab")
                {
                    truncated = AppendPreviewText(builder, "\t", maxChars) || truncated;
                }
                else if (node.Name == WordNamespace + "br" || node.Name == WordNamespace + "cr")
                {
                    truncated = AppendPreviewText(builder, Environment.NewLine, maxChars) || truncated;
                }

                if (truncated)
                {
                    break;
                }
            }

            if (truncated)
            {
                break;
            }

            truncated = AppendPreviewText(builder, Environment.NewLine + Environment.NewLine, maxChars) || truncated;
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "文档没有可预览的文本内容。";
        }

        if (truncated)
        {
            text += Environment.NewLine + Environment.NewLine + "......（内容过长，已截断预览）";
        }

        return text;
    }

    private static bool AppendPreviewText(StringBuilder builder, string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (builder.Length >= maxChars)
        {
            return true;
        }

        var remainingLength = maxChars - builder.Length;
        if (value.Length <= remainingLength)
        {
            builder.Append(value);
            return false;
        }

        builder.Append(value.AsSpan(0, remainingLength));
        return true;
    }

    private sealed class WordStyleInfo
    {
        public string? Name { get; set; }
        public string? Alignment { get; set; }
        public int? BeforeSpacingTwips { get; set; }
        public int? AfterSpacingTwips { get; set; }
        public int? LeftIndentTwips { get; set; }
        public int? RightIndentTwips { get; set; }
        public int? FirstLineTwips { get; set; }
        public string? FontFamily { get; set; }
        public string? Color { get; set; }
        public string? HighlightColor { get; set; }
        public int? FontSizeHalfPoints { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }
        public bool? Underline { get; set; }
        public bool? Strike { get; set; }
    }

    private sealed class WordRelationshipInfo
    {
        public string Type { get; set; } = "";
        public string Target { get; set; } = "";
        public string? TargetMode { get; set; }
    }

    private sealed class WordRenderContext
    {
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, string> _imageCache = new(StringComparer.OrdinalIgnoreCase);

        public WordRenderContext(
            ZipArchive archive,
            Dictionary<string, WordStyleInfo> styles,
            Dictionary<string, WordRelationshipInfo> relationships)
        {
            _archive = archive;
            Styles = styles;
            Relationships = relationships;
        }

        public Dictionary<string, WordStyleInfo> Styles { get; }

        public Dictionary<string, WordRelationshipInfo> Relationships { get; }

        public string? GetHyperlinkTarget(string? relationshipId)
        {
            if (string.IsNullOrWhiteSpace(relationshipId)
                || !Relationships.TryGetValue(relationshipId, out var relationship)
                || !relationship.Type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return relationship.Target;
        }

        public string? GetImageDataUri(string? relationshipId)
        {
            if (string.IsNullOrWhiteSpace(relationshipId))
            {
                return null;
            }

            if (_imageCache.TryGetValue(relationshipId, out var cached))
            {
                return cached;
            }

            if (!Relationships.TryGetValue(relationshipId, out var relationship)
                || !relationship.Type.EndsWith("/image", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var entryPath = ResolveZipPath("word", relationship.Target);
            var entry = _archive.GetEntry(entryPath);
            if (entry == null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            var bytes = memoryStream.ToArray();
            var contentType = GetImageContentType(Path.GetExtension(entry.FullName));
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return null;
            }

            var dataUri = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
            _imageCache[relationshipId] = dataUri;
            return dataUri;
        }

        private static string ResolveZipPath(string baseDirectory, string target)
        {
            var segments = new List<string>();
            foreach (var segment in $"{baseDirectory}/{target}".Replace('\\', '/').Split('/'))
            {
                if (string.IsNullOrWhiteSpace(segment) || segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }

                segments.Add(segment);
            }

            return string.Join("/", segments);
        }

        private static string? GetImageContentType(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => null
            };
        }
    }
}
