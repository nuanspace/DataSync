using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 自定义消息处理器接口
/// </summary>
public interface IMessageHandler
{
    /// <summary>
    /// 处理消息
    /// </summary>
    Task<ProcessResult> HandleAsync(EsbMessage message, EsbInterfaceConfig config);
}
