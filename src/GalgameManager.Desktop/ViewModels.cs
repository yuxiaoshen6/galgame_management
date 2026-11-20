using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.RegularExpressions;
using GalgameManager.Application;

namespace GalgameManager.Desktop;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Notify([CallerMemberName] string property = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(property);
        return true;
    }
}

public sealed class GameItem(Game game) : ObservableModel
{
    private Availability availability = new(GameState.Checking);
    private bool launching;

    public Game Game { get; } = game;
    public string DisplayName => Game.DisplayName;
    public string Directory => Game.InstallDirectory;
    public string? CoverPath => Game.Metadata?.CoverPath;
    public string SummaryPreview => GameMetadataText.Preview(Game.Metadata?.Summary);
    public string Initial
    {
        get
        {
            var separator = DisplayName.IndexOf(' ');
            var name = separator > 0 && DisplayName[..separator].All(char.IsAsciiDigit)
                ? DisplayName[separator..].TrimStart() : DisplayName;
            return System.Globalization.StringInfo.GetNextTextElement(name);
        }
    }
    public int Accent => (int)(Game.Id % 3);
    public string? Reason => availability.Reason;
    public GameState State => availability.State;
    public bool CanLaunch => State == GameState.Ready && !launching;
    public string Status => State switch
    {
        GameState.Ready => "已就绪",
        GameState.NeedsSetup => "待配置",
        GameState.Unavailable => "路径不可用",
        _ => "检查中"
    };
    public string LaunchLabel => launching ? "启动中…" : "启动";

    public bool Launching
    {
        get => launching;
        set
        {
            if (Set(ref launching, value))
            {
                Notify(nameof(CanLaunch));
                Notify(nameof(LaunchLabel));
            }
        }
    }

    public void Update(Availability state)
    {
        availability = state;
        Notify(nameof(State));
        Notify(nameof(Status));
        Notify(nameof(Reason));
        Notify(nameof(CanLaunch));
    }
}

public sealed record EntryOption(EntryCandidate? Entry)
{
    public string Caption => Entry is null ? "待配置 · 稍后选择入口" :
        Entry.RelativePath + (Entry.Auxiliary ? " · 辅助程序" : "");
}

public sealed class PreviewItem : ObservableModel
{
    private bool selected;
    private EntryOption entry;
    private GameMetadata metadata;

    public PreviewItem(GameCandidate candidate)
    {
        Candidate = candidate;
        metadata = candidate.Metadata;
        Entries = new[] { new EntryOption(null) }.Concat(candidate.Entries.Select(item => new EntryOption(item))).ToArray();
        entry = Entries.First(option => option.Entry == candidate.Suggested);
        selected = candidate.Existing is null;
    }

    public GameCandidate Candidate { get; }
    public EntryOption[] Entries { get; }
    public bool IsNew => Candidate.Existing is null;
    public string Guidance => Candidate.Existing is { } existing ? $"已入库 · {existing.DisplayName}" :
        Candidate.Suggested is not null ? "已预选建议入口，可改选" : "请选择入口，或以待配置状态入库";
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public EntryOption Entry { get => entry; set => Set(ref entry, value); }
    public GameMetadata Metadata
    {
        get => metadata;
        set
        {
            if (Set(ref metadata, value))
            {
                Notify(nameof(CanLookupMetadata));
                Notify(nameof(LookupLabel));
                Notify(nameof(SummaryPreview));
            }
        }
    }
    public bool CanLookupMetadata => IsNew && (!Metadata.IsComplete || Metadata.ProviderId is not null);
    public string LookupLabel => Metadata.ProviderId is null ? "联网查找封面和简介" : "重新匹配游戏资料";
    public string SummaryPreview => GameMetadataText.Preview(Metadata.Summary);
}

public sealed class ArgumentItem(string value) : ObservableModel
{
    private string text = value;
    public string Value { get => text; set => Set(ref text, value); }
}

public sealed class EditorModel : ObservableModel
{
    private string name;
    private string directory;
    private string? executable;
    private string? workDirectory;
    private string error = "";
    private bool workDirectoryConfirmed = true;
    private GameMetadata metadata;

    public EditorModel(GameItem? item, string installDirectory, GameMetadata? localMetadata = null)
    {
        Original = item;
        directory = installDirectory;
        name = item?.DisplayName ?? LocalPaths.Name(installDirectory);
        executable = item?.Game.Executable;
        workDirectory = item?.Game.WorkDirectory;
        metadata = item?.Game.Metadata ?? localMetadata ?? new GameMetadata();
        Arguments = new ObservableCollection<ArgumentItem>((item?.Game.Arguments ?? []).Select(arg => new ArgumentItem(arg)));
    }

    public GameItem? Original { get; }
    public bool IsExisting => Original is not null;
    public string Heading => IsExisting ? "游戏详情" : "手动添加";
    public string Title => IsExisting ? Original!.DisplayName : Name;
    public string Name
    {
        get => name;
        set { if (Set(ref name, value)) Notify(nameof(Title)); }
    }
    public string Directory { get => directory; set => Set(ref directory, value); }
    public string? Executable { get => executable; set => Set(ref executable, value); }
    public string? WorkDirectory { get => workDirectory; set => Set(ref workDirectory, value); }
    public string Error
    {
        get => error;
        set => Set(ref error, value);
    }
    public bool NeedsWorkDirectoryConfirmation => !workDirectoryConfirmed;
    public ObservableCollection<ArgumentItem> Arguments { get; }
    public GameMetadata Metadata
    {
        get => metadata;
        set
        {
            if (Set(ref metadata, value))
            {
                Notify(nameof(Summary));
                Notify(nameof(CoverPath));
                Notify(nameof(MetadataSource));
                Notify(nameof(NeedsMetadata));
            }
        }
    }
    public string Summary
    {
        get => Metadata.Summary ?? "";
        set => Metadata = Metadata with { Summary = value };
    }
    public string? CoverPath => Metadata.CoverPath;
    public bool NeedsMetadata => !Metadata.IsComplete;
    public string? MetadataSource => Metadata.ProviderId is { } id
        ? $"已关联 {Metadata.Provider} · {id}" : null;

    public void Relocate(string path, string? entry)
    {
        Directory = path;
        Executable = entry;
        ConfirmWorkDirectory(confirmed: WorkDirectory is null);
        Error = entry is null ? "请为新安装目录选择启动入口。" : "";
    }

    public void ConfirmWorkDirectory(bool confirmed = true)
    {
        workDirectoryConfirmed = confirmed;
        Notify(nameof(NeedsWorkDirectoryConfirmation));
    }

    public GameDraft ToDraft()
    {
        if (!workDirectoryConfirmed)
            throw new LibraryException("请确认继续使用当前工作目录，或恢复默认。");
        if (Original is not null && !Directory.Equals(Original.Directory, StringComparison.OrdinalIgnoreCase) &&
            Original.Game.RelativeExecutable is not null && Executable is null)
            throw new LibraryException("请为新安装目录选择 .exe 启动入口。");
        return new GameDraft(Original?.Game.Id, Name, Directory, Executable, WorkDirectory,
            Arguments.Select(arg => arg.Value).ToArray(), Metadata);
    }
}

internal static partial class GameMetadataText
{
    private const int PreviewLength = 105;

    public static string Preview(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return "暂无游戏简介";

        var normalized = new StringInfo(Whitespace().Replace(summary.Trim(), " "));
        return normalized.LengthInTextElements > PreviewLength
            ? normalized.SubstringByTextElements(0, PreviewLength) + "…"
            : normalized.String;
    }

    public static string SearchTitle(string title) => EditionSuffix().Replace(title.Trim(), "").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?:(?:\s*[-–]?\s*[\[【(（]|\s+)(?:汉化版|中文版|免安装版?|绿色版)[\]】)）]?\s*)+$")]
    private static partial Regex EditionSuffix();
}
