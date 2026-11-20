namespace GalgameManager.Application;

public sealed class LibraryService(IGameRepository games, IScanRootRepository roots, IFileScanner scanner,
    IGameProcessLauncher launcher, IGameMetadataFiles metadataFiles)
{
    private static readonly HashSet<string> ProgramDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "binary", "binaries", "x64", "x86", "win64", "win32", "win", "windows"
    };

    public Task<IReadOnlyList<Game>> ListGamesAsync() => Task.Run(games.ListGames);

    public Task<IReadOnlyList<ScanRoot>> ListRootsAsync() => Task.Run(roots.ListRoots);

    public Task AddRootsAsync(IReadOnlyList<string> paths) => Task.Run(() =>
    {
        var normalized = paths.Select(LocalPaths.Normalize).ToArray();
        foreach (var path in normalized)
            LocalPaths.RequireDirectory(path, "扫描目录");
        roots.AddRoots(normalized);
    });

    public Task RemoveRootAsync(long id) => Task.Run(() => roots.RemoveRoot(id));

    public Task<ScanPreview> ScanRootsAsync(IReadOnlyList<ScanRoot> selectedRoots, IProgress<ScanProgress>? progress,
        CancellationToken cancellation) => Task.Run(() =>
    {
        var scan = scanner.Scan(selectedRoots, progress, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var existing = games.ListGames();
        var byInstallation = existing.ToDictionary(game => game.InstallDirectory, StringComparer.OrdinalIgnoreCase);
        var byEntry = existing.Where(game => game.Executable is not null)
            .ToDictionary(game => game.Executable!, StringComparer.OrdinalIgnoreCase);
        var orderedRoots = selectedRoots.OrderByDescending(root => root.Path.Length).ToArray();
        var groups = scan.Files.GroupBy(file =>
        {
            cancellation.ThrowIfCancellationRequested();
            return GroupDirectory(file.Path, orderedRoots);
        }, StringComparer.OrdinalIgnoreCase);
        var result = new List<GameCandidate>();

        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = LocalPaths.Name(group.Key);
            var entries = group.Select(file =>
            {
                var fileName = Path.GetFileNameWithoutExtension(file.Path);
                var atRoot = group.Key.Equals(Path.GetDirectoryName(file.Path), StringComparison.OrdinalIgnoreCase);
                var score = (fileName.Equals(name, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
                    + (file.HasDataDirectory || scan.ArchiveDirectories.Contains(group.Key) ? 3 : 0)
                    + (atRoot ? 1 : 0);
                return new EntryCandidate(file.Path, Path.GetRelativePath(group.Key, file.Path), score, IsAuxiliary(fileName));
            }).OrderBy(entry => entry.Auxiliary).ThenByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();

            var credible = entries.TakeWhile(entry => !entry.Auxiliary).Take(2).ToArray();
            if (credible.Length == 0)
                continue;

            var suggested = credible[0].Score >= 4 &&
                (credible.Length == 1 || credible[0].Score - credible[1].Score >= 2) ? credible[0] : null;
            var match = byInstallation.GetValueOrDefault(group.Key) ??
                entries.Select(entry => byEntry.GetValueOrDefault(entry.Path)).FirstOrDefault(game => game is not null);
            var source = orderedRoots.First(root => LocalPaths.Contains(root.Path, group.Key));
            var metadata = match is null ? metadataFiles.ReadLocal(group.Key) : match.Metadata ?? new();
            result.Add(new GameCandidate(group.Key, name, source.Id, entries, suggested, match, metadata));
        }

        return new ScanPreview(result.OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(game => game.InstallDirectory, StringComparer.OrdinalIgnoreCase).ToArray(),
            scan.Issues, scan.Directories, scan.Skipped);
    }, cancellation);

    public Task<ImportResult> ImportAsync(IReadOnlyList<ImportChoice> choices) => Task.Run(() =>
    {
        var batch = new List<Game>(choices.Count);
        foreach (var choice in choices)
        {
            var item = choice.Candidate;
            try
            {
                batch.Add(Validate(new GameDraft(null, item.DisplayName, item.InstallDirectory, choice.Entry?.Path,
                    null, [], choice.Metadata ?? item.Metadata), item.SourceRootId, allowMissingEntry: true));
            }
            catch (LibraryException ex)
            {
                throw new LibraryException($"「{item.DisplayName}」 · {item.InstallDirectory}\n{ex.Message}");
            }
        }

        return games.ImportGames(batch);
    });

    public Task SaveGameAsync(GameDraft draft) => Task.Run(() =>
    {
        var game = Validate(draft, null, allowMissingEntry: draft.Id.HasValue);
        games.SaveGame(game);
    });

    public Task RemoveGameAsync(long id) => Task.Run(() => games.RemoveGame(id));

    public async Task CheckGamesAsync(IReadOnlyList<Game> items, IProgress<(long Id, Availability State)[]> progress,
        CancellationToken cancellation)
    {
        await Task.Run(() =>
        {
            var batch = new List<(long, Availability)>(32);
            foreach (var game in items)
            {
                cancellation.ThrowIfCancellationRequested();
                batch.Add((game.Id, Check(game)));
                if (batch.Count == 32)
                {
                    progress.Report(batch.ToArray());
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                progress.Report(batch.ToArray());
        }, cancellation).ConfigureAwait(false);
    }

    public Task LaunchAsync(long gameId) => Task.Run(() =>
    {
        var game = games.FindGame(gameId) ?? throw new LibraryException("游戏记录已不存在，请刷新游戏库。");
        var spec = GetLaunchSpec(game) ?? throw new LibraryException("请在详情中选择启动入口。");
        launcher.Start(spec);
    });

    public Task<string?> MatchRelocatedEntryAsync(Game game, string newDirectory) => Task.Run(() =>
    {
        if (game.RelativeExecutable is not { } relative)
            return null;
        var entry = Path.Combine(newDirectory, relative);
        try
        {
            LocalPaths.RelativeEntry(newDirectory, entry);
            return entry;
        }
        catch (LibraryException)
        {
            return null;
        }
    });

    private Game Validate(GameDraft draft, long? source, bool allowMissingEntry)
    {
        var name = draft.DisplayName.Trim();
        if (name.Length == 0)
            throw new LibraryException("请填写显示名称。");
        var directory = LocalPaths.Normalize(draft.InstallDirectory);
        LocalPaths.RequireDirectory(directory, "安装目录");
        var entry = string.IsNullOrWhiteSpace(draft.Executable) ? null : LocalPaths.RelativeEntry(directory, draft.Executable);
        if (entry is null && !allowMissingEntry)
            throw new LibraryException("请通过「选择文件」配置 .exe 启动入口。");
        var work = string.IsNullOrWhiteSpace(draft.WorkDirectory) ? null : LocalPaths.Normalize(draft.WorkDirectory);
        if (work is not null)
            LocalPaths.RequireDirectory(work, "工作目录");
        if (draft.Arguments.Any(argument => argument.Contains('\0')))
            throw new LibraryException("启动参数不能包含空字符。");
        var metadata = draft.Metadata;
        if (metadata is not null)
            metadata = metadata with { Summary = metadata.Summary?.Trim(), CoverPath = metadataFiles.CacheCover(metadata.CoverPath) };
        return new Game(draft.Id.GetValueOrDefault(), source, name, directory, entry, work, draft.Arguments, metadata);
    }

    private static Availability Check(Game game)
    {
        try
        {
            return GetLaunchSpec(game) is null
                ? new Availability(GameState.NeedsSetup, "请在详情中选择启动入口。")
                : new Availability(GameState.Ready);
        }
        catch (LibraryException ex)
        {
            return new Availability(GameState.Unavailable, ex.Message);
        }
    }

    private static LaunchSpec? GetLaunchSpec(Game game)
    {
        LocalPaths.RequireDirectory(game.InstallDirectory, "安装目录");
        if (game.WorkDirectory is { } customWorkDirectory)
            LocalPaths.RequireDirectory(customWorkDirectory, "工作目录");
        if (game.Executable is not { } entry)
            return null;
        LocalPaths.RelativeEntry(game.InstallDirectory, entry);
        var work = game.WorkDirectory ?? Path.GetDirectoryName(entry)!;
        return new LaunchSpec(entry, work, game.Arguments);
    }

    private static string GroupDirectory(string entry, ScanRoot[] roots)
    {
        var directory = Path.GetDirectoryName(entry)!;
        var broadestRoot = roots.Last(root => LocalPaths.Contains(root.Path, directory));
        while (!directory.Equals(broadestRoot.Path, StringComparison.OrdinalIgnoreCase) &&
            ProgramDirectories.Contains(Path.GetFileName(directory)))
        {
            directory = Path.GetDirectoryName(directory)!;
        }
        return directory;
    }

    private static bool IsAuxiliary(string name)
    {
        var normalized = name.ToUpperInvariant();
        return normalized is "SETUP" or "INSTALL" or "INSTALLER" or "UNINSTALL" or "CONFIG" or "CONFIGURATION"
            or "SETTINGS" or "UPDATER" or "UPDATE" or "CRASHREPORTER" or "CRASHPAD_HANDLER" or "DXSETUP"
            or "VC_REDIST.X64" or "VC_REDIST.X86" or "VCREDIST"
            || normalized.StartsWith("UNINS", StringComparison.Ordinal)
            || normalized.StartsWith("UNITYCRASHHANDLER", StringComparison.Ordinal);
    }
}
