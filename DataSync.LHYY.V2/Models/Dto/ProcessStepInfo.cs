namespace DataSync.LHYY.V2.Models.Dto;

/// <summary>
/// 处理步骤信息（内存收集 + JSON 序列化）
/// </summary>
public class ProcessStepInfo
{
    public string Step { get; set; } = "";
    public bool IsSuccess { get; set; }
    public int ElapsedMs { get; set; }
    public string? Detail { get; set; }
}
