namespace DataSync.Common.FollowUp;

public sealed class FollowUpStorageStatus
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool Ready { get; init; }
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }
    public long DirectoryBytes { get; init; }
    public int UsedPercent { get; init; }
    public int WarningUsedPercent { get; init; }
    public int CriticalUsedPercent { get; init; }
    public string? Error { get; init; }

    public bool IsCritical => Ready && UsedPercent >= CriticalUsedPercent;
    public bool IsWarning => Ready && !IsCritical && UsedPercent >= WarningUsedPercent;
}

public static class FollowUpStorageInspector
{
    public static FollowUpStorageStatus Inspect(
        string name,
        string path,
        int warningUsedPercent,
        int criticalUsedPercent,
        bool calculateDirectoryBytes = true)
    {
        var warning = Math.Clamp(warningUsedPercent, 1, 98);
        var critical = Math.Clamp(criticalUsedPercent, warning + 1, 99);
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"存储目录不存在：{fullPath}");
            var drive = DriveInfo.GetDrives()
                .Where(candidate => candidate.IsReady && IsWithin(fullPath, candidate.RootDirectory.FullName))
                .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
                .FirstOrDefault()
                ?? new DriveInfo(Path.GetPathRoot(fullPath)
                    ?? throw new InvalidOperationException("无法确定存储目录所在文件系统。"));
            var total = drive.TotalSize;
            var available = drive.AvailableFreeSpace;
            return new FollowUpStorageStatus
            {
                Name = name,
                Path = fullPath,
                Ready = drive.IsReady,
                TotalBytes = total,
                AvailableBytes = available,
                DirectoryBytes = calculateDirectoryBytes ? SumFiles(fullPath) : 0,
                UsedPercent = total <= 0 ? 0 : (int)Math.Round((total - available) * 100d / total),
                WarningUsedPercent = warning,
                CriticalUsedPercent = critical
            };
        }
        catch (Exception ex)
        {
            return new FollowUpStorageStatus
            {
                Name = name,
                Path = path,
                WarningUsedPercent = warning,
                CriticalUsedPercent = critical,
                Error = ex.Message
            };
        }
    }

    public static string ValidateManagedFile(string root, string path, string requiredExtension)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison)
            || !string.Equals(Path.GetExtension(fullPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("待清理文件不在受控目录内或扩展名不符合要求。");
        }

        RejectReparsePoints(fullRoot, fullPath);
        return fullPath;
    }

    public static string ValidateManagedDirectory(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, PathComparison) || fullPath.Equals(fullRoot, PathComparison))
            throw new InvalidOperationException("待清理目录不在受控目录内。");

        RejectReparsePoints(fullRoot, fullPath);
        return fullPath;
    }

    private static long SumFiles(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            total = checked(total + new FileInfo(file).Length);
        return total;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
            return Path.IsPathRooted(normalizedPath);

        return normalizedPath.Equals(normalizedRoot, PathComparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static void RejectReparsePoints(string fullRoot, string fullPath)
    {
        var current = fullPath;
        while (true)
        {
            FileSystemInfo[] candidates = [new DirectoryInfo(current), new FileInfo(current)];
            foreach (var candidate in candidates)
            {
                try
                {
                    candidate.Refresh();
                    if (candidate.LinkTarget is not null
                        || candidate.Exists && candidate.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new InvalidOperationException("受控清理路径不允许包含符号链接或重解析点。");
                }
                catch (FileNotFoundException)
                {
                    // 尚未创建的普通路径组件允许继续校验其父目录。
                }
                catch (DirectoryNotFoundException)
                {
                    // 尚未创建的普通路径组件允许继续校验其父目录。
                }
            }

            if (current.Equals(fullRoot, PathComparison)) break;
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException("无法验证受控清理路径。");
            if (!current.StartsWith(fullRoot, PathComparison))
                throw new InvalidOperationException("受控清理路径越界。");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
