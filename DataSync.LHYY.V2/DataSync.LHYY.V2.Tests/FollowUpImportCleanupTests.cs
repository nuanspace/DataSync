using DataSync.LHYY.V2.Services.FollowUp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class FollowUpImportCleanupTests
{
    [Fact]
    public void 导入流程结束时清理解压目录()
    {
        var stagingPath = Path.Combine(Path.GetTempPath(), $"followup-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        File.WriteAllText(Path.Combine(stagingPath, "payload.json"), "{}");

        try
        {
            FollowUpPackageImportService.CleanupStaging(
                stagingPath,
                NullLogger<FollowUpPackageImportService>.Instance);

            Assert.False(Directory.Exists(stagingPath));
        }
        finally
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
        }
    }
}
