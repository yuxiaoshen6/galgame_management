namespace GalgameManager.Application;

public sealed record GameMetadata(string? Summary = null, string? CoverPath = null,
    string? Provider = null, string? ProviderId = null)
{
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasCover => !string.IsNullOrWhiteSpace(CoverPath);
    public bool IsComplete => HasSummary && HasCover;

    public GameMetadata FillMissing(GameMetadata other)
    {
        var useSummary = !HasSummary && other.HasSummary;
        var useCover = !HasCover && other.HasCover;
        if (!useSummary && !useCover)
            return this;

        return new GameMetadata(useSummary ? other.Summary : Summary, useCover ? other.CoverPath : CoverPath,
            other.Provider ?? Provider, other.ProviderId ?? ProviderId);
    }
}

public interface IGameMetadataFiles
{
    GameMetadata ReadLocal(string installDirectory);
    string? CacheCover(string? path);
}
