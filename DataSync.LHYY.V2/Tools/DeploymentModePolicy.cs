using Microsoft.Extensions.Configuration;

namespace DataSync.LHYY.V2.Tools;

public static class DeploymentModePolicy
{
    public const string ExternalCube = "external-cube";
    public const string FreshCube = "fresh-cube";
    public const string ExternalCubeUpgradeBlockedMessage =
        "external-cube 模式禁止由 LHYY 升级模块初始化、执行基础库恢复或升级现有 CubeDb；请由目标库负责人按正式发布流程处理。";

    public static bool IsExternalCube(IConfiguration configuration) =>
        !string.Equals(
            configuration["Deployment:Mode"],
            FreshCube,
            StringComparison.OrdinalIgnoreCase);
}
