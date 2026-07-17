namespace DataSync.CYYY.Services.FollowUp;

public static class FollowUpPackageRetryPolicy
{
    public static string[] DatabaseStatuses => ["Pending", "Failed", "Pulling"];

    public static bool IsRetryable(string? status) => status is "Pending" or "Failed" or "Pulling";
}
