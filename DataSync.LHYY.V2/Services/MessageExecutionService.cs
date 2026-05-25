using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 统一消息执行服务
/// </summary>
public class MessageExecutionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MessageExecutionService> _logger;

    public MessageExecutionService(IServiceProvider serviceProvider, ILogger<MessageExecutionService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ExecuteAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        try
        {
            if (config.HandlerType == HandlerType.GenericQuestionWriteBack)
            {
                var processor = _serviceProvider.GetRequiredService<GenericQuestionWriteBackProcessor>();
                return await processor.ProcessAsync(message, config);
            }

            if (config.HandlerType == HandlerType.Custom && !string.IsNullOrEmpty(config.HandlerName))
                return await ExecuteCustomHandlerAsync(message, config);

            var genericProcessor = _serviceProvider.GetRequiredService<GenericMessageProcessor>();
            return await genericProcessor.ProcessAsync(message, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "消息处理异常: MessageId={MessageId}, TranCode={TranCode}", message.MessageId, message.TranCode);
            return ProcessResult.Fail($"处理异常: {ex.Message}");
        }
    }

    private async Task<ProcessResult> ExecuteCustomHandlerAsync(EsbMessage message, EsbInterfaceConfig config)
    {
        var handlerType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .FirstOrDefault(t => t.Name == config.HandlerName && typeof(IMessageHandler).IsAssignableFrom(t));

        if (handlerType == null)
            return ProcessResult.Fail($"未找到自定义处理器: {config.HandlerName}");

        var handler = (IMessageHandler)ActivatorUtilities.CreateInstance(_serviceProvider, handlerType);
        return await handler.HandleAsync(message, config);
    }
}
