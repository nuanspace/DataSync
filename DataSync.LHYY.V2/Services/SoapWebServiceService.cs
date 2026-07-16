using System.Xml;
using System.Xml.Linq;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 将 SOAP 1.1 字符串 XML 请求转换为内部 JSON 消息。
/// </summary>
public sealed class SoapWebServiceService
{
    private const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string WsdlNamespace = "http://schemas.xmlsoap.org/wsdl/";
    private const string WsdlSoapNamespace = "http://schemas.xmlsoap.org/wsdl/soap/";
    private const string XmlSchemaNamespace = "http://www.w3.org/2001/XMLSchema";

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly EsbReceiverService _receiverService;
    private readonly ILogger<SoapWebServiceService> _logger;

    public SoapWebServiceService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        EsbReceiverService receiverService,
        ILogger<SoapWebServiceService> logger)
    {
        _contextFactory = contextFactory;
        _receiverService = receiverService;
        _logger = logger;
    }

    public static void NormalizeConfiguration(EsbInterfaceConfig config)
    {
        config.SoapServiceCode = string.IsNullOrWhiteSpace(config.SoapServiceCode)
            ? null
            : config.SoapServiceCode.Trim();
        config.SoapOperation = string.IsNullOrWhiteSpace(config.SoapOperation)
            ? config.TranCode.Trim()
            : config.SoapOperation.Trim();
        config.SoapAction = string.IsNullOrWhiteSpace(config.SoapAction) && config.SoapServiceCode != null
            ? BuildDefaultSoapAction(config.SoapServiceCode, config.SoapOperation)
            : config.SoapAction?.Trim();
    }

    public async Task<string?> ValidateConfigurationAsync(
        EsbInterfaceConfig config,
        CancellationToken cancellationToken = default)
    {
        if (!config.SoapEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(config.IntegrationProjectCode))
            return "WebService 只能绑定到项目专属接口配置";
        if (string.IsNullOrWhiteSpace(config.SoapServiceCode))
            return "开放 WebService 时必须填写服务代码";
        if (!System.Text.RegularExpressions.Regex.IsMatch(config.SoapServiceCode, "^[A-Za-z0-9_-]+$"))
            return "服务代码只能包含字母、数字、下划线和短横线";

        try
        {
            XmlConvert.VerifyNCName(config.SoapOperation!);
        }
        catch
        {
            return "操作名不是有效的 XML 名称";
        }

        if (string.IsNullOrWhiteSpace(config.SoapAction))
            return "开放 WebService 时必须配置 SOAPAction";

        var serviceCode = config.SoapServiceCode.ToLower();
        var operation = config.SoapOperation!.ToLower();
        var action = config.SoapAction!.ToLower();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var duplicated = await db.EsbInterfaceConfigs
            .AsNoTracking()
            .AnyAsync(item => item.Id != config.Id
                              && item.SoapEnabled
                              && item.SoapServiceCode != null
                              && item.SoapOperation != null
                              && item.SoapAction != null
                              && item.SoapServiceCode.ToLower() == serviceCode
                              && (item.SoapOperation.ToLower() == operation
                                  || item.SoapAction.ToLower() == action),
                cancellationToken);

        return duplicated ? "同一 WebService 下的操作名或 SOAPAction 已存在" : null;
    }

    public static bool TryConvertSampleXmlToInternalJson(
        string tranCode,
        string sampleXml,
        out string json,
        out string errorMessage)
    {
        json = "";
        errorMessage = "";

        try
        {
            var document = ParseXml(sampleXml);
            XNamespace soap = SoapEnvelopeNamespace;
            if (document.Root?.Name == soap + "Envelope")
            {
                var operationElement = document.Root.Element(soap + "Body")?.Elements().FirstOrDefault()
                    ?? throw new InvalidOperationException("SOAP Body 中缺少操作节点");
                var inputElement = operationElement.Elements()
                    .FirstOrDefault(element => string.Equals(element.Name.LocalName, "INPUTPARA", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("SOAP 请求中缺少 INPUTPARA 参数");
                document = ParseXml(ReadXmlParameterValue(inputElement));
            }
            else if (document.Root?.Elements()
                         .FirstOrDefault(element => string.Equals(element.Name.LocalName, "INPUTPARA", StringComparison.OrdinalIgnoreCase)) is { } inputElement)
            {
                document = ParseXml(ReadXmlParameterValue(inputElement));
            }

            json = JToken.Parse(ConvertToInternalJson(tranCode, document))
                .ToString(Newtonsoft.Json.Formatting.Indented);
            return true;
        }
        catch (Exception ex) when (ex is XmlException or JsonException or InvalidOperationException)
        {
            errorMessage = $"XML 模板格式错误：{ex.Message}";
            return false;
        }
    }

    public async Task<string?> BuildWsdlAsync(
        string serviceCode,
        string endpointUrl,
        CancellationToken cancellationToken = default)
    {
        var actions = await GetActionsAsync(serviceCode, cancellationToken);
        if (actions.Count == 0)
            return null;

        var configuredServiceCode = actions[0].Config.SoapServiceCode!;
        var targetNamespace = BuildTargetNamespace(configuredServiceCode);
        var serviceName = XmlConvert.EncodeLocalName(configuredServiceCode) + "Service";
        XNamespace wsdl = WsdlNamespace;
        XNamespace soap = WsdlSoapNamespace;
        XNamespace xsd = XmlSchemaNamespace;
        XNamespace tns = targetNamespace;

        var schema = new XElement(xsd + "schema",
            new XAttribute("targetNamespace", targetNamespace),
            new XAttribute("elementFormDefault", "qualified"));
        var definitions = new XElement(wsdl + "definitions",
            new XAttribute("name", serviceName),
            new XAttribute("targetNamespace", targetNamespace),
            new XAttribute(XNamespace.Xmlns + "wsdl", wsdl),
            new XAttribute(XNamespace.Xmlns + "soap", soap),
            new XAttribute(XNamespace.Xmlns + "xsd", xsd),
            new XAttribute(XNamespace.Xmlns + "tns", tns),
            new XElement(wsdl + "types", schema));

        var portType = new XElement(wsdl + "portType", new XAttribute("name", serviceName + "Soap"));
        var binding = new XElement(wsdl + "binding",
            new XAttribute("name", serviceName + "Soap"),
            new XAttribute("type", "tns:" + serviceName + "Soap"),
            new XElement(soap + "binding",
                new XAttribute("transport", "http://schemas.xmlsoap.org/soap/http"),
                new XAttribute("style", "document")));

        foreach (var action in actions)
        {
            var responseName = action.Operation + "Response";
            var resultName = action.Operation + "Result";

            schema.Add(
                BuildSchemaElement(xsd, action.Operation, "INPUTPARA"),
                BuildSchemaElement(xsd, responseName, resultName));
            definitions.Add(
                BuildWsdlMessage(wsdl, action.Operation + "SoapIn", action.Operation),
                BuildWsdlMessage(wsdl, action.Operation + "SoapOut", responseName));
            portType.Add(new XElement(wsdl + "operation",
                new XAttribute("name", action.Operation),
                new XElement(wsdl + "input", new XAttribute("message", "tns:" + action.Operation + "SoapIn")),
                new XElement(wsdl + "output", new XAttribute("message", "tns:" + action.Operation + "SoapOut"))));
            binding.Add(new XElement(wsdl + "operation",
                new XAttribute("name", action.Operation),
                new XElement(soap + "operation",
                    new XAttribute("soapAction", action.Action),
                    new XAttribute("style", "document")),
                new XElement(wsdl + "input", new XElement(soap + "body", new XAttribute("use", "literal"))),
                new XElement(wsdl + "output", new XElement(soap + "body", new XAttribute("use", "literal")))));
        }

        definitions.Add(
            portType,
            binding,
            new XElement(wsdl + "service",
                new XAttribute("name", serviceName),
                new XElement(wsdl + "port",
                    new XAttribute("name", serviceName + "Soap"),
                    new XAttribute("binding", "tns:" + serviceName + "Soap"),
                    new XElement(soap + "address", new XAttribute("location", endpointUrl)))));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), definitions)
            .ToString(SaveOptions.DisableFormatting);
    }

    public async Task<SoapWebServiceResult> ProcessAsync(
        string serviceCode,
        string? soapActionHeader,
        string requestXml,
        CancellationToken cancellationToken = default)
    {
        var actions = await GetActionsAsync(serviceCode, cancellationToken);
        if (actions.Count == 0)
            return SoapWebServiceResult.Fault("soap:Client", "WebService 服务不存在或未启用");

        XDocument envelope;
        try
        {
            envelope = ParseXml(requestXml);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return SoapWebServiceResult.Fault("soap:Client", "SOAP XML 格式错误");
        }

        XNamespace soap = SoapEnvelopeNamespace;
        if (envelope.Root?.Name != soap + "Envelope")
            return SoapWebServiceResult.Fault("soap:VersionMismatch", "仅支持 SOAP 1.1");

        var operationElement = envelope.Root.Element(soap + "Body")?.Elements().FirstOrDefault();
        if (operationElement == null)
            return SoapWebServiceResult.Fault("soap:Client", "SOAP Body 中缺少操作节点");

        var headerAction = NormalizeSoapAction(soapActionHeader);
        var action = string.IsNullOrWhiteSpace(headerAction)
            ? actions.FirstOrDefault(item => string.Equals(item.Operation, operationElement.Name.LocalName, StringComparison.OrdinalIgnoreCase))
            : actions.FirstOrDefault(item => string.Equals(item.Action, headerAction, StringComparison.OrdinalIgnoreCase));

        if (action == null)
            return SoapWebServiceResult.Fault("soap:Client", "未找到对应的 SOAPAction");

        if (!string.Equals(action.Operation, operationElement.Name.LocalName, StringComparison.OrdinalIgnoreCase))
            return SoapWebServiceResult.Fault("soap:Client", "SOAPAction 与操作节点不一致");

        try
        {
            var inputElement = operationElement.Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "INPUTPARA", StringComparison.OrdinalIgnoreCase));
            if (inputElement == null)
                return BuildOperationResult(action, false, "缺少 INPUTPARA 参数");

            var businessXml = ReadXmlParameterValue(inputElement);
            if (string.IsNullOrWhiteSpace(businessXml))
                return BuildOperationResult(action, false, "INPUTPARA 参数为空");

            var businessDocument = ParseXml(businessXml);
            var rawJson = ConvertToInternalJson(action.Config.TranCode, businessDocument);
            await _receiverService.ProcessAsync(
                rawJson,
                action.Config.IntegrationProjectCode!,
                cancellationToken);

            return BuildOperationResult(action, true, "成功");
        }
        catch (Exception ex) when (ex is XmlException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "SOAP 业务 XML 处理失败，服务 {ServiceCode}，操作 {Operation}", serviceCode, action.Operation);
            return BuildOperationResult(action, false, "业务 XML 格式错误");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SOAP 请求处理失败，服务 {ServiceCode}，操作 {Operation}", serviceCode, action.Operation);
            return BuildOperationResult(action, false, "系统内部错误");
        }
    }

    private async Task<List<SoapActionConfig>> GetActionsAsync(
        string serviceCode,
        CancellationToken cancellationToken)
    {
        var normalizedServiceCode = serviceCode.Trim().ToUpper();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var configs = await db.EsbInterfaceConfigs
            .AsNoTracking()
            .Where(config => config.IsEnabled
                             && config.SoapEnabled
                             && config.IntegrationProjectCode != null
                             && config.SoapServiceCode != null
                             && config.SoapServiceCode.ToUpper() == normalizedServiceCode)
            .OrderBy(config => config.SortOrder)
            .ThenBy(config => config.TranCode)
            .ToListAsync(cancellationToken);

        var actions = configs.Select(config =>
        {
            var operation = string.IsNullOrWhiteSpace(config.SoapOperation)
                ? config.TranCode
                : config.SoapOperation.Trim();
            XmlConvert.VerifyNCName(operation);

            return new SoapActionConfig(
                config,
                operation,
                string.IsNullOrWhiteSpace(config.SoapAction)
                    ? BuildDefaultSoapAction(config.SoapServiceCode!, operation)
                    : config.SoapAction.Trim());
        }).ToList();

        if (actions.GroupBy(item => item.Operation, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || actions.GroupBy(item => item.Action, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("同一 WebService 下的操作名或 SOAPAction 重复");
        }

        return actions;
    }

    private static XElement BuildSchemaElement(XNamespace xsd, string elementName, string valueElementName)
        => new(xsd + "element",
            new XAttribute("name", elementName),
            new XElement(xsd + "complexType",
                new XElement(xsd + "sequence",
                    new XElement(xsd + "element",
                        new XAttribute("name", valueElementName),
                        new XAttribute("type", "xsd:string")))));

    private static XElement BuildWsdlMessage(XNamespace wsdl, string messageName, string elementName)
        => new(wsdl + "message",
            new XAttribute("name", messageName),
            new XElement(wsdl + "part",
                new XAttribute("name", "parameters"),
                new XAttribute("element", "tns:" + elementName)));

    private static SoapWebServiceResult BuildOperationResult(SoapActionConfig action, bool success, string message)
    {
        var responseValue = new XElement("RESPONSE",
            new XElement("RESULT_CODE", success ? "true" : "false"),
            new XElement("RESULT_CONTENT", message))
            .ToString(SaveOptions.DisableFormatting);
        XNamespace soap = SoapEnvelopeNamespace;
        XNamespace operationNamespace = BuildTargetNamespace(action.Config.SoapServiceCode!);
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap),
                new XElement(soap + "Body",
                    new XElement(operationNamespace + (action.Operation + "Response"),
                        new XElement(operationNamespace + (action.Operation + "Result"), responseValue)))));

        return new SoapWebServiceResult(
            StatusCodes.Status200OK,
            envelope.ToString(SaveOptions.DisableFormatting));
    }

    private static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 100L * 1024 * 1024
        };
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, LoadOptions.None);
    }

    private static string ReadXmlParameterValue(XElement inputElement)
        => inputElement.HasElements
            ? string.Concat(inputElement.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
            : inputElement.Value;

    private static string ConvertToInternalJson(string tranCode, XDocument businessDocument)
    {
        var converted = JsonConvert.SerializeXNode(businessDocument, Newtonsoft.Json.Formatting.None, false);
        var businessObject = JObject.Parse(converted);
        var result = new JObject { ["serverCode"] = tranCode };
        foreach (var property in businessObject.Properties())
            result.Add(property.Name, property.Value);

        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string NormalizeSoapAction(string? value)
        => value?.Trim().Trim('"') ?? "";

    public static string BuildDefaultSoapAction(string serviceCode, string operation)
        => $"{BuildTargetNamespace(serviceCode)}/{operation}";

    public static string BuildTargetNamespace(string serviceCode)
        => $"urn:datasync:webservice:{serviceCode.Trim()}";

    private sealed record SoapActionConfig(
        EsbInterfaceConfig Config,
        string Operation,
        string Action);
}

public sealed record SoapWebServiceResult(int StatusCode, string Content)
{
    public static SoapWebServiceResult Fault(string faultCode, string message)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap),
                new XElement(soap + "Body",
                    new XElement(soap + "Fault",
                        new XElement("faultcode", faultCode),
                        new XElement("faultstring", message)))));

        return new SoapWebServiceResult(
            StatusCodes.Status500InternalServerError,
            envelope.ToString(SaveOptions.DisableFormatting));
    }
}
