namespace WorkerDock.ControlCenter.Registry;

public static class CentralStorageOptions
{
    public static string ValidateDataDirectory(string? configuredPath, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
            throw new InvalidOperationException("CentralStorageDataDirectoryMustBeAbsolute");

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath));
        var normalizedContentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));

        if (IsSameOrDescendant(path, normalizedContentRoot) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("wwwroot", StringComparison.OrdinalIgnoreCase) ||
                                segment.Equals("releases", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("CentralStorageDataDirectoryMustBeOutsideApplicationFiles");

        return path;
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
