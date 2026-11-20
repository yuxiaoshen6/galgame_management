using System.ComponentModel;
using GalgameManager.Application;

namespace GalgameManager.Desktop;

public enum Page { Library, Roots, Preview }

public sealed class MainViewModel(LibraryService service, Action initialize) : ObservableModel
{
    private GameItem[] games = [];
    private GameItem[][] rows = [];
    private ScanRoot[] roots = [];
    private PreviewItem[] preview = [];
    private ScanIssue[] issues = [];
    private CancellationTokenSource? checkCancellation;
    private CancellationTokenSource? scanCancellation;
    private Page page;
    private string keyword = "";
    private string notice = "正在打开本地游戏库…";
    private string scanSummary = "";
    private string currentDirectory = "";
    private string scanCounts = "";
    private bool isBusy;
    private bool isLoaded;
    private bool isCanceling;
    private bool hasPreview;
    private bool hasError;
    private EditorModel? editor;
    private int columns = 3;
    private readonly HashSet<long> launching = [];

    public Page Page
    {
        get => page;
        set
        {
            if (Set(ref page, value))
            {
                Notify(nameof(OnLibrary));
                Notify(nameof(OnRoots));
                Notify(nameof(OnPreview));
            }
        }
    }
    public bool OnLibrary => Page == Page.Library;
    public bool OnRoots => Page == Page.Roots;
    public bool OnPreview => Page == Page.Preview;
    public string Keyword
    {
        get => keyword;
        set { if (Set(ref keyword, value)) Filter(); }
    }
    public int Columns => columns;
    public int Total => games.Length;
    public int VisibleCount { get; private set; }
    public GameItem[][] Rows { get => rows; private set => Set(ref rows, value); }
    public bool LibraryEmpty => IsLoaded && Total == 0;
    public bool NoMatches => Total > 0 && VisibleCount == 0;
    public string ResultCount => $"{VisibleCount} / {Total} 款";
    public ScanRoot[] Roots { get => roots; private set => Set(ref roots, value); }
    public bool HasRoots => Roots.Length > 0;
    public bool NoRoots => !HasRoots;
    public PreviewItem[] Preview { get => preview; private set => Set(ref preview, value); }
    public bool HasCandidates => Preview.Length > 0;
    public ScanIssue[] Issues { get => issues; private set => Set(ref issues, value); }
    public bool HasIssues => Issues.Length > 0;
    public bool HasPreview { get => hasPreview; private set => Set(ref hasPreview, value); }
    public string ScanSummary { get => scanSummary; private set => Set(ref scanSummary, value); }
    public string CurrentDirectory { get => currentDirectory; private set => Set(ref currentDirectory, value); }
    public string ScanCounts { get => scanCounts; private set => Set(ref scanCounts, value); }
    public int SelectedCount => Preview.Count(item => item.Selected);
    public string ImportLabel => $"确认导入（{SelectedCount}）";
    public bool CanImport => SelectedCount > 0 && IsAvailable;
    public string Notice { get => notice; private set => Set(ref notice, value); }
    public bool HasError { get => hasError; private set => Set(ref hasError, value); }
    public bool IsScanning => scanCancellation is not null;
    public bool CanCancelScan => IsScanning && !isCanceling;
    public string CancelLabel => isCanceling ? "正在取消…" : "取消扫描";
    public bool IsLoaded { get => isLoaded; private set => Set(ref isLoaded, value); }
    public bool IsAvailable => IsLoaded && !IsBusy;
    public bool CanScan => IsAvailable && !IsScanning && HasRoots;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value))
                NotifyActions();
        }
    }
    public EditorModel? Editor { get => editor; set => Set(ref editor, value); }

    public void SetColumns(int value)
    {
        if (columns == value)
            return;
        columns = value;
        Notify(nameof(Columns));
        Filter();
    }

    public async Task InitializeOrRefreshAsync() => await RunAsync(async () =>
    {
        if (!IsLoaded)
        {
            await Task.Run(initialize);
            await ReloadRootsAsync();
            IsLoaded = true;
        }
        await ReloadGamesAsync();
        ShowNotice($"本地游戏库已载入 · {Total} 款游戏");
    });

    public Task AddRootsAsync(IReadOnlyList<string> paths) => RunAsync(async () =>
    {
        await service.AddRootsAsync(paths);
        await ReloadRootsAsync();
        Page = Page.Roots;
        ShowNotice("已保存扫描目录，可扫描全部或选择单个目录。");
    });

    public Task RemoveRootAsync(ScanRoot root) => RunAsync(async () =>
    {
        await service.RemoveRootAsync(root.Id);
        await ReloadRootsAsync();
        ShowNotice("已移除扫描目录。");
    });

    public async Task StartScanAsync(IReadOnlyList<ScanRoot> selection)
    {
        if (!CanScan || selection.Count == 0)
            return;

        using var operation = new CancellationTokenSource();
        scanCancellation = operation;
        isCanceling = false;
        CurrentDirectory = selection[0].Path;
        ScanCounts = "已遍历 0 个目录 · 0 个候选 · 跳过 0 项";
        NotifyScanning();
        ShowNotice("正在扫描，可以继续浏览游戏库。");

        var progress = new Progress<ScanProgress>(item =>
        {
            if (scanCancellation != operation)
                return;
            CurrentDirectory = item.CurrentDirectory;
            ScanCounts = $"已遍历 {item.Directories} 个目录 · {item.Executables} 个候选 · 跳过 {item.Skipped} 项";
        });
        try
        {
            var result = await service.ScanRootsAsync(selection, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            SetPreview(result);
            if (OnRoots && Editor is null)
                Page = Page.Preview;
            ShowNotice(ScanSummary, result.Directories == 0 && HasIssues);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("扫描已取消，本次结果已放弃。");
        }
        catch (LibraryException ex)
        {
            ShowNotice(ex.Message, true);
        }
        finally
        {
            scanCancellation = null;
            NotifyScanning();
        }
    }

    public void CancelScan()
    {
        isCanceling = true;
        scanCancellation?.Cancel();
        NotifyScanning();
        ShowNotice("正在取消，等待当前文件读取结束…");
    }

    public void SelectPreview(bool value)
    {
        foreach (var item in Preview)
            item.Selected = value && item.IsNew;
    }

    public void DiscardPreview()
    {
        SetPreview(null);
        Page = Page.Roots;
        ShowNotice("已放弃导入预览。");
    }

    public Task ImportAsync() => RunAsync(async () =>
    {
        var items = Preview.Where(item => item.Selected)
            .Select(item => new ImportChoice(item.Candidate, item.Entry.Entry, item.Metadata)).ToArray();
        var existing = Preview.Count(item => !item.IsNew);
        var result = await service.ImportAsync(items);
        SetPreview(null);
        Page = Page.Library;
        ShowNotice($"已导入 {result.Added} 款游戏 · 跳过 {result.Skipped + existing} 条已有记录");
        await ReloadGamesAsync();
    });

    public async Task<bool> SaveEditorAsync(EditorModel model)
    {
        if (!IsAvailable)
            return false;
        IsBusy = true;
        model.Error = "";
        try
        {
            await service.SaveGameAsync(model.ToDraft());
        }
        catch (LibraryException ex)
        {
            model.Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }

        await RunAsync(async () =>
        {
            await ReloadGamesAsync();
            ShowNotice("游戏配置已保存。");
        });
        return true;
    }

    public async Task<bool> RemoveGameAsync(EditorModel model)
    {
        if (!IsAvailable || model.Original is not { } item)
            return false;

        var id = item.Game.Id;
        if (launching.Contains(id))
        {
            model.Error = "正在启动这款游戏，请稍后移除。";
            return false;
        }

        IsBusy = true;
        model.Error = "";
        try
        {
            await service.RemoveGameAsync(id);
            SetGames(games.Where(game => game.Game.Id != id).ToArray());
            ShowNotice($"已从游戏库移除「{item.DisplayName}」。");
            return true;
        }
        catch (LibraryException ex)
        {
            model.Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RelocateAsync(EditorModel model, string path)
    {
        if (!IsAvailable)
            return;
        IsBusy = true;
        try
        {
            var entry = await service.MatchRelocatedEntryAsync(model.Original!.Game, path);
            model.Relocate(path, entry);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LaunchAsync(GameItem item)
    {
        var id = item.Game.Id;
        if (!item.CanLaunch || !launching.Add(id))
            return;
        item.Launching = true;
        ShowNotice($"正在启动「{item.DisplayName}」…");
        try
        {
            await service.LaunchAsync(id);
            ShowNotice($"Windows 已为「{item.DisplayName}」创建进程。");
        }
        catch (LibraryException ex)
        {
            ShowNotice(ex.Message, true);
            await RunAsync(ReloadGamesAsync);
        }
        finally
        {
            launching.Remove(id);
            item.Launching = false;
            var current = Array.Find(games, game => game.Game.Id == id);
            if (current is not null)
                current.Launching = false;
        }
    }

    public void Stop()
    {
        checkCancellation?.Cancel();
        scanCancellation?.Cancel();
    }

    private async Task ReloadRootsAsync()
    {
        Roots = (await service.ListRootsAsync()).ToArray();
        Notify(nameof(HasRoots));
        Notify(nameof(NoRoots));
        NotifyActions();
    }

    private async Task ReloadGamesAsync()
    {
        var records = await service.ListGamesAsync();
        var items = records.OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(game => game.InstallDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(game => new GameItem(game) { Launching = launching.Contains(game.Id) }).ToArray();
        SetGames(items);
    }

    private void SetGames(GameItem[] items)
    {
        checkCancellation?.Cancel();
        games = items;
        Notify(nameof(Total));
        Notify(nameof(LibraryEmpty));
        Filter();
        var pending = items.Where(item => item.State == GameState.Checking).Select(item => item.Game).ToArray();
        if (pending.Length > 0)
            _ = CheckGamesAsync(pending);
    }

    private async Task CheckGamesAsync(IReadOnlyList<Game> records)
    {
        using var operation = new CancellationTokenSource();
        checkCancellation = operation;
        var currentItems = games;
        var index = currentItems.ToDictionary(game => game.Game.Id);
        var progress = new Progress<(long Id, Availability State)[]>(batch =>
        {
            if (!operation.IsCancellationRequested && ReferenceEquals(currentItems, games))
            {
                foreach (var (id, state) in batch)
                    index[id].Update(state);
            }
        });
        try { await service.CheckGamesAsync(records, progress, operation.Token); }
        catch (OperationCanceledException) { }
        finally { if (checkCancellation == operation) checkCancellation = null; }
    }

    private void Filter()
    {
        var filter = Keyword.Trim();
        var visible = games.Where(game => game.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        VisibleCount = visible.Length;
        Rows = visible.Chunk(columns).ToArray();
        Notify(nameof(VisibleCount));
        Notify(nameof(NoMatches));
        Notify(nameof(ResultCount));
    }

    private void SetPreview(ScanPreview? result)
    {
        foreach (var item in Preview)
            item.PropertyChanged -= PreviewChanged;
        Preview = result?.Games.Select(item => new PreviewItem(item)).ToArray() ?? [];
        foreach (var item in Preview)
            item.PropertyChanged += PreviewChanged;
        Issues = result?.Issues.ToArray() ?? [];
        HasPreview = result is not null;
        if (result is not null)
        {
            var overview = result.Directories == 0 && HasIssues ? "无法访问所选目录，请检查连接或权限。" :
                !HasCandidates ? "本次扫描未发现游戏。可尝试其他目录或手动添加。" :
                $"发现 {Preview.Length} 款游戏 · {Preview.Count(item => !item.IsNew)} 款已入库";
            ScanSummary = $"{overview}  已遍历 {result.Directories} 个目录，跳过 {result.Skipped} 项。";
        }

        Notify(nameof(HasCandidates));
        Notify(nameof(HasIssues));
        PreviewChanged(null, new PropertyChangedEventArgs(nameof(PreviewItem.Selected)));
    }

    private void PreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewItem.Selected))
            return;
        Notify(nameof(SelectedCount));
        Notify(nameof(ImportLabel));
        Notify(nameof(CanImport));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try { await action(); }
        catch (LibraryException ex) { ShowNotice(ex.Message, true); }
        finally { IsBusy = false; }
    }

    private void ShowNotice(string message, bool error = false)
    {
        HasError = error;
        Notice = message;
    }

    private void NotifyActions()
    {
        Notify(nameof(IsAvailable));
        Notify(nameof(CanScan));
        Notify(nameof(CanImport));
    }

    private void NotifyScanning()
    {
        Notify(nameof(IsScanning));
        Notify(nameof(CanCancelScan));
        Notify(nameof(CancelLabel));
        Notify(nameof(CanScan));
    }
}
