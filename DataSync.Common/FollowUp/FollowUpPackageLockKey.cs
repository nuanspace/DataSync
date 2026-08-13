namespace DataSync.Common.FollowUp;

public static class FollowUpPackageLockKey
{
    public static string Create(string hospitalCode, string packageId)
    {
        if (string.IsNullOrWhiteSpace(hospitalCode)) throw new ArgumentException("医院编码不能为空。", nameof(hospitalCode));
        if (string.IsNullOrWhiteSpace(packageId)) throw new ArgumentException("包标识不能为空。", nameof(packageId));
        return $"followup-package:{hospitalCode.Trim().ToUpperInvariant()}:{packageId.Trim()}";
    }
}
