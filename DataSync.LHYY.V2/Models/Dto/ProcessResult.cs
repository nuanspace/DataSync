using System.Diagnostics;
using DataSync.LHYY.V2.Models.Enums;

namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 消息处理结果
/// </summary>
public class ProcessResult
{
    public bool IsSuccess { get; set; }
    public bool IsFiltered { get; set; }
    public string Message { get; set; } = "";
    public Guid? PatientId { get; set; }
    public Guid? EventId { get; set; }
    public MessageStatus? OverrideStatus { get; set; }

    /// <summary>
    /// 处理步骤列表，最终一次性写入数据库
    /// </summary>
    public List<ProcessStepInfo> Steps { get; set; } = [];

    public void AddStep(string step, bool isSuccess, string? detail = null, int elapsedMs = 0)
    {
        Steps.Add(new ProcessStepInfo
        {
            Step = step,
            IsSuccess = isSuccess,
            ElapsedMs = elapsedMs,
            Detail = detail,
        });
    }

    public async Task<T> LogStepAsync<T>(string step, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await action();
            sw.Stop();
            AddStep(step, true, null, (int)sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AddStep(step, false, ex.Message, (int)sw.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<T> ExtractWithLogAsync<T>(string step, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();
        AddStep(step, true, null, (int)sw.ElapsedMilliseconds);
        return result;
    }

    public static ProcessResult Success(string message = "处理成功", Guid? patientId = null, Guid? eventId = null)
        => new() { IsSuccess = true, Message = message, PatientId = patientId, EventId = eventId };

    public static ProcessResult Fail(string message)
        => new() { IsSuccess = false, Message = message };

    public static ProcessResult Filtered(string? reason = null)
        => new() { IsFiltered = true, Message = reason ?? "被过滤规则跳过" };

    public static ProcessResult Deferred(string message)
        => new() { Message = message, OverrideStatus = MessageStatus.Pending };
}
