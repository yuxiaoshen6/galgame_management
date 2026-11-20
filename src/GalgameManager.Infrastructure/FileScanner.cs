using System.Diagnostics;
using GalgameManager.Application;

namespace GalgameManager.Infrastructure;

public sealed class FileScanner(DiagnosticLog log) : IFileScanner
{
    public FileScan Scan(IReadOnlyList<ScanRoot> roots, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FoundExecutable>();
        var issues = new List<ScanIssue>();
        var pending = new Stack<string>(roots.OrderByDescending(root => root.Path.Length).Select(root => root.Path));
        var time = Stopwatch.StartNew();
        var directories = 0;
        var skipped = 0;
        var last = "";

        while (pending.TryPop(out var directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(directory))
                continue;

            if (time.ElapsedMilliseconds >= 90 || last.Length == 0)
            {
                progress?.Report(new ScanProgress(directory, directories, files.Count, skipped));
                time.Restart();
            }
            last = directory;

            var executables = new List<string>();
            var dataDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped++;
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry.FullName);
                        if (entry.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                            dataDirectories.Add(entry.Name);
                    }
                    else if (entry.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        executables.Add(entry.FullName);
                    }
                    else if (entry.Extension.Equals(".xp3", StringComparison.OrdinalIgnoreCase))
                    {
                        archives.Add(directory);
                    }

                    if (time.ElapsedMilliseconds >= 90)
                    {
                        progress?.Report(new ScanProgress(directory, directories, files.Count + executables.Count, skipped));
                        time.Restart();
                    }
                }

                directories++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                issues.Add(new ScanIssue(directory, LocalPaths.AccessReason(ex)));
                log.Write("scan-directory", ex);
            }

            foreach (var entry in executables)
            {
                cancellation.ThrowIfCancellationRequested();
                files.Add(new FoundExecutable(entry, dataDirectories.Contains(Path.GetFileNameWithoutExtension(entry) + "_Data")));
            }
        }

        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(last, directories, files.Count, skipped));
        return new FileScan(files, archives, issues, directories, skipped);
    }
}
