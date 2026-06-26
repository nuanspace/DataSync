namespace DataSync.Common.Ocr;

/// <summary>
/// PDF OCR 转换服务。
/// </summary>
public interface IOcrConversionService
{
    Task<OcrDocumentResult> ConvertAsync(
        OcrSource source,
        OcrConversionOptions options,
        CancellationToken cancellationToken = default);

    Task<OcrDocumentResult> ConvertToJsonFileAsync(
        OcrSource source,
        OcrConversionOptions options,
        string outputJsonPath,
        CancellationToken cancellationToken = default);
}
