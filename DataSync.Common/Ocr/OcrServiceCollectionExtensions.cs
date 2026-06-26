using DataSync.Common.Ocr.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace DataSync.Common.Ocr;

/// <summary>
/// OCR 服务注册扩展。
/// </summary>
public static class OcrServiceCollectionExtensions
{
    public static IServiceCollection AddDataSyncOcr(this IServiceCollection services)
    {
        services.AddSingleton<OcrSourceResolver>();
        services.AddSingleton<PdfToPpmRenderer>();
        services.AddSingleton<TesseractCliOcrEngine>();
        services.AddSingleton<IOcrConversionService, OcrConversionService>();
        return services;
    }
}
