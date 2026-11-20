namespace GalgameManager.Application;

public static class LocalPaths
{
    public static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new LibraryException("请选择绝对路径。");

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            throw new LibraryException("路径格式无效。");
        }
    }

    public static string Key(string path) => Normalize(path).ToUpperInvariant();

    public static bool Contains(string directory, string path)
    {
        if (string.Equals(directory, path, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string Name(string directory)
    {
        var name = Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(name) ? directory : name;
    }

    public static void RequireDirectory(string directory, string label)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
                throw new LibraryException($"{label}不是文件夹，请重新选择。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException($"{label}{AccessReason(ex)}，请检查磁盘连接或重新选择。");
        }
    }

    public static string RelativeEntry(string directory, string entry)
    {
        entry = Normalize(entry);
        if (!Contains(directory, entry) || directory.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new LibraryException("请选择安装目录内的 .exe 启动入口。");

        var relative = Path.GetRelativePath(directory, entry);
        var current = directory;
        try
        {
            // A nested link can redirect an apparently contained executable outside the installation.
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new LibraryException("启动入口经过目录联接或符号链接，请选择安装目录内的实际文件。");
                if (string.Equals(current, entry, StringComparison.OrdinalIgnoreCase) &&
                    (attributes & FileAttributes.Directory) != 0)
                    throw new LibraryException("启动入口是文件夹，请重新选择 .exe 文件。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LibraryException($"启动入口{AccessReason(ex)}，请重新选择 .exe 文件。");
        }

        return relative;
    }

    public static string AccessReason(Exception error) => error switch
    {
        UnauthorizedAccessException => "没有访问权限",
        DirectoryNotFoundException or FileNotFoundException or DriveNotFoundException => "已不存在或磁盘未连接",
        PathTooLongException => "路径过长",
        _ => "无法读取"
    };
}
