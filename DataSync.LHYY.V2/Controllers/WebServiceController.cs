using System.Text;
using DataSync.LHYY.V2.Services;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.LHYY.V2.Controllers;

/// <summary>
/// 可配置 SOAP 1.1 WebService 入口。
/// </summary>
[ApiController]
[Route("webservice/{serviceCode}")]
public sealed class WebServiceController : ControllerBase
{
    private readonly SoapWebServiceService _soapService;
    private readonly ILogger<WebServiceController> _logger;

    public WebServiceController(
        SoapWebServiceService soapService,
        ILogger<WebServiceController> logger)
    {
        _soapService = soapService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetWsdl(string serviceCode, CancellationToken cancellationToken)
    {
        if (!Request.Query.ContainsKey("wsdl"))
            return NotFound();

        var endpointUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}";
        var wsdl = await _soapService.BuildWsdlAsync(serviceCode, endpointUrl, cancellationToken);
        return wsdl == null
            ? NotFound()
            : Content(wsdl, "text/xml; charset=utf-8", Encoding.UTF8);
    }

    [HttpPost]
    public async Task<IActionResult> Receive(string serviceCode, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var requestXml = await reader.ReadToEndAsync(cancellationToken);
            var result = await _soapService.ProcessAsync(
                serviceCode,
                Request.Headers["SOAPAction"].ToString(),
                requestXml,
                cancellationToken);

            return new ContentResult
            {
                StatusCode = result.StatusCode,
                ContentType = "text/xml; charset=utf-8",
                Content = result.Content
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebService 请求处理失败，服务 {ServiceCode}", serviceCode);
            var result = SoapWebServiceResult.Fault("soap:Server", "系统内部错误");
            return new ContentResult
            {
                StatusCode = result.StatusCode,
                ContentType = "text/xml; charset=utf-8",
                Content = result.Content
            };
        }
    }
}
