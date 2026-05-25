using System.IO.Compression;
using System.Text;
using DataSync.LHYY.V2.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.LHYY.V2.Controllers;

/// <summary>
/// ESB 数据接收唯一入口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EsbController : ControllerBase
{
    private const long DefaultMaxRequestBodyBytes = 100L * 1024 * 1024;

    private readonly EsbReceiverService _receiverService;
    private readonly ILogger<EsbController> _logger;
    private readonly long _maxRequestBodyBytes;

    public EsbController(
        EsbReceiverService receiverService,
        ILogger<EsbController> logger,
        IConfiguration configuration)
    {
        _receiverService = receiverService;
        _logger = logger;
        _maxRequestBodyBytes = ResolveMaxRequestBodyBytes(configuration);
    }

    /// <summary>
    /// POST /api/esb — 接收 ESB 推送的 JSON 数据
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        try
        {
            ApplyRequestBodyLimit();

            if (Request.ContentLength > _maxRequestBodyBytes)
                return Ok(BuildEsbErrorResponse($"请求体超过限制：最大 {_maxRequestBodyBytes} 字节"));

            var rawJson = await ReadRequestBodyAsync(cancellationToken);
            if (rawJson.Length > _maxRequestBodyBytes)
                return Ok(BuildEsbErrorResponse($"请求体超过限制：最大 {_maxRequestBodyBytes} 字节"));

            if (string.IsNullOrWhiteSpace(rawJson) && Request.Headers.ContainsKey("X-Codex-Skip-Empty"))
            {
                _logger.LogWarning("收到空请求体");
                return Ok(new
                {
                    Response = new
                    {
                        Head = new { AckCode = "200.2", AckMessage = "请求体为空" },
                        Body = "",
                    }
                });
            }

            _logger.LogDebug("收到 ESB 请求，长度: {Length}", rawJson.Length);

            var response = await _receiverService.ProcessAsync(rawJson, cancellationToken);
            return Ok(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ESB 请求已取消");
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 ESB 请求时发生未知错误");
            return Ok(new
            {
                Response = new
                {
                    Head = new { AckCode = "300.1", AckMessage = "系统内部错误" },
                    Body = "",
                }
            });
        }
    }

    /// <summary>
    /// 读取请求体，支持 gzip 解压
    /// </summary>
    private async Task<string> ReadRequestBodyAsync(CancellationToken cancellationToken)
    {
        var contentEncoding = Request.Headers.ContentEncoding.ToString();
        Stream bodyStream = Request.Body;

        if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            bodyStream = new GZipStream(Request.Body, CompressionMode.Decompress);
        }

        using var reader = new StreamReader(bodyStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private void ApplyRequestBodyLimit()
    {
        var feature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = _maxRequestBodyBytes;
    }

    private static long ResolveMaxRequestBodyBytes(IConfiguration configuration)
    {
        var configured = configuration.GetValue<long?>("Esb:MaxRequestBodyBytes");
        return configured is > 0 ? configured.Value : DefaultMaxRequestBodyBytes;
    }

    private static object BuildEsbErrorResponse(string message)
    {
        return new
        {
            Response = new
            {
                Head = new { AckCode = "300.1", AckMessage = message },
                Body = "",
            }
        };
    }
}
