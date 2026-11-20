namespace GalgameManager.Application;

public sealed record ScanRoot(long Id, string Path);

public sealed record Game(long Id, long? SourceRootId, string DisplayName, string InstallDirectory,
    string? RelativeExecutable, string? WorkDirectory, IReadOnlyList<string> Arguments, GameMetadata? Metadata = null)
{
    public string? Executable => RelativeExecutable is null ? null : Path.Combine(InstallDirectory, RelativeExecutable);
}

public sealed record GameDraft(long? Id, string DisplayName, string InstallDirectory,
    string? Executable, string? WorkDirectory, IReadOnlyList<string> Arguments, GameMetadata? Metadata = null);

public enum GameState { Checking, Ready, NeedsSetup, Unavailable }
public sealed record Availability(GameState State, string? Reason = null);
public sealed record LaunchSpec(string Executable, string WorkDirectory, IReadOnlyList<string> Arguments);

public sealed record ScanProgress(string CurrentDirectory, int Directories, int Executables, int Skipped);
public sealed record ScanIssue(string Path, string Reason);
public sealed record FoundExecutable(string Path, bool HasDataDirectory);
public sealed record FileScan(IReadOnlyList<FoundExecutable> Files, IReadOnlySet<string> ArchiveDirectories,
    IReadOnlyList<ScanIssue> Issues, int Directories, int Skipped);

public sealed record EntryCandidate(string Path, string RelativePath, int Score, bool Auxiliary);
public sealed record GameCandidate(string InstallDirectory, string DisplayName, long? SourceRootId,
    IReadOnlyList<EntryCandidate> Entries, EntryCandidate? Suggested, Game? Existing, GameMetadata Metadata);
public sealed record ScanPreview(IReadOnlyList<GameCandidate> Games, IReadOnlyList<ScanIssue> Issues, int Directories, int Skipped);
public sealed record ImportChoice(GameCandidate Candidate, EntryCandidate? Entry, GameMetadata? Metadata = null);
public sealed record ImportResult(int Added, int Skipped);

public sealed class LibraryException(string message) : Exception(message);

public interface IFileScanner
{
    FileScan Scan(IReadOnlyList<ScanRoot> roots, IProgress<ScanProgress>? progress, CancellationToken cancellation);
}

public interface IGameRepository
{
    IReadOnlyList<Game> ListGames();
    Game? FindGame(long id);
    void SaveGame(Game game);
    void RemoveGame(long id);
    ImportResult ImportGames(IReadOnlyList<Game> games);
}

public interface IScanRootRepository
{
    IReadOnlyList<ScanRoot> ListRoots();
    void AddRoots(IReadOnlyList<string> paths);
    void RemoveRoot(long id);
}

public interface IGameProcessLauncher
{
    void Start(LaunchSpec spec);
}
