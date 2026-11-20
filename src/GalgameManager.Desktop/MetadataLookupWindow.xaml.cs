using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GalgameManager.Application;
using GalgameManager.Infrastructure;

namespace GalgameManager.Desktop;

public partial class MetadataLookupWindow : Window
{
    private readonly MetadataCatalog catalog;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? coverRequest;
    private string? selectedCover;

    public GameMetadata? Result { get; private set; }

    public MetadataLookupWindow(MetadataCatalog catalog, string title)
    {
        this.catalog = catalog;
        InitializeComponent();
        var cleaned = GameMetadataText.SearchTitle(title);
        QueryBox.Text = cleaned.Length > 0 ? cleaned : title;
        SearchStatus.Text = "选择资料源后搜索。查询仅使用输入的游戏名。";
        Loaded += (_, _) => { QueryBox.Focus(); QueryBox.SelectAll(); };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    private async void Search(object sender, RoutedEventArgs e)
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0 || !SearchButton.IsEnabled)
        {
            if (query.Length == 0)
                SearchStatus.Text = "请输入游戏名称。";
            return;
        }

        SearchButton.IsEnabled = SourceBox.IsEnabled = false;
        Results.ItemsSource = null;
        SearchStatus.Text = "正在查找游戏资料…";
        try
        {
            var provider = SourceBox.SelectedIndex == 1 ? MetadataProvider.Vndb : MetadataProvider.Bangumi;
            var matches = await catalog.SearchAsync(provider, query, lifetime.Token);
            if (!IsVisible)
                return;
            Results.ItemsSource = matches;
            SearchStatus.Text = matches.Count == 0
                ? "没有匹配的作品，可修改名称或切换资料源。"
                : $"找到 {matches.Count} 个候选。请核对名称和发行日期。";
        }
        catch (Exception ex) when (IsLookupError(ex))
        {
            if (IsVisible)
                SearchStatus.Text = ex is LibraryException ? ex.Message : "暂时无法查找资料，可修改名称、切换资料源或稍后重试。";
        }
        finally
        {
            if (IsVisible)
                SearchButton.IsEnabled = SourceBox.IsEnabled = true;
        }
    }

    private async void ShowCandidate(object sender, SelectionChangedEventArgs e)
    {
        coverRequest?.Cancel();
        selectedCover = null;
        CandidateCover.Source = null;
        CoverStatus.Text = "";
        UseButton.IsEnabled = false;
        var selected = Results.SelectedItem as CatalogGame;
        CandidatePane.DataContext = selected;
        if (selected is null)
            return;
        if (selected.CoverUrl is null)
        {
            CoverStatus.Text = "暂无封面";
            UseButton.IsEnabled = !string.IsNullOrWhiteSpace(selected.Summary);
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        coverRequest = request;
        CoverStatus.Text = "正在加载封面…";
        try
        {
            var path = await catalog.DownloadCoverAsync(selected, request.Token);
            if (request.IsCancellationRequested || !IsVisible)
                return;
            CandidateCover.Source = CoverSourceConverter.Load(path, 250);
            selectedCover = CandidateCover.Source is not null ? path : null;
            CoverStatus.Text = selectedCover is null ? "暂无可用封面" : "";
        }
        catch (Exception ex) when (IsLookupError(ex))
        {
            if (!request.IsCancellationRequested && IsVisible)
                CoverStatus.Text = ex is LibraryException ? ex.Message : "封面加载失败，可使用简介或稍后再试。";
        }
        finally
        {
            if (coverRequest == request)
                coverRequest = null;
            if (!request.IsCancellationRequested && IsVisible)
                UseButton.IsEnabled = selectedCover is not null || !string.IsNullOrWhiteSpace(selected.Summary);
        }
    }

    private void Apply(object sender, RoutedEventArgs e)
    {
        if (UseButton.IsEnabled && Results.SelectedItem is CatalogGame selected)
        {
            var provider = selected.Provider == MetadataProvider.Bangumi ? "Bangumi" : "VNDB";
            Result = new GameMetadata(selected.Summary, selectedCover, provider, selected.Id);
            DialogResult = true;
        }
    }

    private static bool IsLookupError(Exception ex) => ex is LibraryException or HttpRequestException or
        IOException or UnauthorizedAccessException or OperationCanceledException or JsonException;
}
