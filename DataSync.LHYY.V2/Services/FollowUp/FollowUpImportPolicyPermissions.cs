namespace DataSync.LHYY.V2.Services.FollowUp;

internal static class FollowUpImportPolicyPermissions
{
    internal static string[] GetRequiredColumnPrivileges(string importPolicy) => importPolicy switch
    {
        "UseExistingById" or "RejectIfMissing" => ["SELECT"],
        "InsertIfMissing" => ["INSERT"],
        // PostgreSQL 的 ON CONFLICT DO UPDATE 同时读取目标列和 EXCLUDED 列。
        "Upsert" => ["INSERT", "UPDATE", "SELECT"],
        _ => throw new InvalidDataException($"不支持的导入策略：{importPolicy}。")
    };

    internal static string BuildColumnPrivilegePredicate(string importPolicy) =>
        string.Join(
            Environment.NewLine,
            GetRequiredColumnPrivileges(importPolicy).Select(privilege =>
                $"AND has_column_privilege(format('%I.%I', table_schema, table_name), column_name, '{privilege}')"));
}
