using System.Runtime.CompilerServices;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpAttachmentDeploymentTests
{
    [Fact]
    public void 生产Compose要求显式绑定NTCare真实附件根()
    {
        var repositoryRoot = GetRepositoryRoot();
        var deploymentRoot = Path.Combine(repositoryRoot, "deploy", "s7-followup-hospital");
        foreach (var composeName in new[] { "docker-compose.yml", "docker-compose.fresh-cube.yml" })
        {
            var compose = File.ReadAllText(Path.Combine(deploymentRoot, composeName));
            Assert.Contains(
                "${NTCARE_UPLOADS_PATH:?请在.env中设置NTCARE_UPLOADS_PATH}:/app/uploads",
                compose,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "${DATA_ROOT:-/data/s7-followup}/uploads:/app/uploads",
                compose,
                StringComparison.Ordinal);
        }

        var environmentExample = File.ReadAllText(Path.Combine(deploymentRoot, ".env.example"));
        Assert.Contains(
            "NTCARE_UPLOADS_PATH=/__REPLACE_WITH_NTCARE_UPLOADS_ABSOLUTE_PATH__",
            environmentExample,
            StringComparison.Ordinal);
        foreach (var scriptName in new[] { "install.sh", "start.sh" })
        {
            var script = File.ReadAllText(Path.Combine(deploymentRoot, scriptName));
            Assert.Contains("validate_ntcare_uploads_path", script, StringComparison.Ordinal);
            Assert.Contains("validate_ntcare_uploads_container_contract", script, StringComparison.Ordinal);
        }
        var deploymentModeScript = File.ReadAllText(Path.Combine(deploymentRoot, "deployment-mode.sh"));
        Assert.Contains("s7_compose run --rm --no-deps --entrypoint sh datasync-lhyy-v2", deploymentModeScript, StringComparison.Ordinal);
        Assert.Contains("ln \"$probe/source\" \"$probe/claim\"", deploymentModeScript, StringComparison.Ordinal);
        Assert.Contains("mv \"$probe/claim\" \"$probe/published\"", deploymentModeScript, StringComparison.Ordinal);
        var installScript = File.ReadAllText(Path.Combine(deploymentRoot, "install.sh"));
        Assert.DoesNotContain("$data_root/uploads", installScript, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
