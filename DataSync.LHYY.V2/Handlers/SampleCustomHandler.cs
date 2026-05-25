using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Services;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Handlers;

/// <summary>
/// 示例自定义处理器
/// 使用方法：在 esb_interface_config 表中设置 handler_type=1(Custom)，handler_name="SampleCustomHandler"
/// </summary>
public class SampleCustomHandler : IMessageHandler
{
    private readonly GenericMessageProcessor _genericProcessor;
    private readonly ILogger<SampleCustomHandler> _logger;

    public SampleCustomHandler(
        GenericMessageProcessor genericProcessor,
        ILogger<SampleCustomHandler> logger)
    {
        _genericProcessor = genericProcessor;
        _logger = logger;
    }

    public async Task<ProcessResult> HandleAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        _logger.LogInformation("自定义处理器开始处理: TranCode={TranCode}, MessageId={MessageId}",
            message.TranCode, message.MessageId);

        // 示例：解析 Body 做自定义前置逻辑
        if (!string.IsNullOrEmpty(message.RawJson) && message.RawJson != "{}")
        {
            try
            {
                if (!MessageJsonHelper.TryParseToken(message.RawJson, out _, out var parseError))
                    return ProcessResult.Fail(parseError ?? "自定义处理器解析失败");

                // 示例：可以在此处做数据校验、转换、补充等自定义逻辑
                // var patientName = body.SelectToken("$.PatientName")?.ToString();
                // if (string.IsNullOrEmpty(patientName))
                //     return ProcessResult.Fail("自定义校验失败：缺少患者姓名");
            }
            catch (Exception ex)
            {
                return ProcessResult.Fail($"自定义处理器解析失败: {ex.Message}");
            }
        }

        // 复用通用处理器完成标准流程（Patient → Event → Question → SubCard）
        var result = await _genericProcessor.ProcessAsync(message, config);

        // 示例：可以在此处做自定义后置逻辑
        if (result.IsSuccess)
        {
            _logger.LogInformation("自定义处理器完成: PatientId={PatientId}, EventId={EventId}",
                result.PatientId, result.EventId);

            // 向步骤列表追加自定义日志
            result.AddStep("自定义后处理", true, "自定义后处理完成");
        }

        return result;
    }
}
