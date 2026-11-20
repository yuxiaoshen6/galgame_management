using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GalgameManager.Application;
using GalgameManager.Infrastructure;
using Microsoft.Win32;

namespace GalgameManager.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private readonly MetadataFiles metadataFiles;
    private readonly MetadataCatalog catalog;
    private FrameworkElement? originCard;
    private Rect? openingRect;
    private IInputElement? returnFocus;
    private bool transitioning;

    public MainWindow(MainViewModel model, MetadataFiles metadataFiles, MetadataCatalog catalog)
    {
        this.model = model;
        this.metadataFiles = metadataFiles;
        this.catalog = catalog;
        InitializeComponent();
        DataContext = model;
        model.PropertyChanged += OnModelChange;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await model.InitializeOrRefreshAsync();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        model.Stop();
        model.PropertyChanged -= OnModelChange;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        model.SetColumns(e.NewSize.Width < 770 ? 2 : 3);
        LibraryBanner.Height = e.NewSize.Height < 520 ? 142 : 183;
    }

    private void OnModelChange(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page) && SystemParameters.ClientAreaAnimation)
        {
            var page = model.Page switch { Page.Roots => RootsPage, Page.Preview => PreviewPage, _ => LibraryPage };
            Animate(page, OpacityProperty, .52, 1, 170);
        }
        if (e.PropertyName == nameof(MainViewModel.IsScanning))
            ScanProgressBar.IsIndeterminate = model.IsScanning && SystemParameters.ClientAreaAnimation;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && model.Editor is not null)
        {
            e.Handled = true;
            _ = CloseEditorAsync();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && model.Editor is null)
        {
            e.Handled = true;
            model.Page = Page.Library;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SearchBox.Focus());
        }
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void ToggleMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void ShowLibrary(object sender, RoutedEventArgs e) => model.Page = Page.Library;
    private void ShowRoots(object sender, RoutedEventArgs e) => model.Page = Page.Roots;
    private void ShowPreview(object sender, RoutedEventArgs e) => model.Page = Page.Preview;
    private async void RefreshLibrary(object sender, RoutedEventArgs e) => await model.InitializeOrRefreshAsync();

    private void ClearSearch(object sender, RoutedEventArgs e)
    {
        model.Keyword = "";
        SearchBox.Focus();
    }

    private async void AddRoots(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择游戏扫描目录", Multiselect = true };
        if (picker.ShowDialog(this) == true)
            await model.AddRootsAsync(picker.FolderNames);
    }

    private async void RemoveRoot(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScanRoot root })
            await model.RemoveRootAsync(root);
    }

    private async void ScanAll(object sender, RoutedEventArgs e) => await model.StartScanAsync(model.Roots);

    private async void ScanOne(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScanRoot root })
            await model.StartScanAsync([root]);
    }

    private void CancelScan(object sender, RoutedEventArgs e) => model.CancelScan();
    private void SelectAllPreview(object sender, RoutedEventArgs e) => model.SelectPreview(true);
    private void ClearPreviewSelection(object sender, RoutedEventArgs e) => model.SelectPreview(false);
    private void DiscardPreview(object sender, RoutedEventArgs e) => model.DiscardPreview();

    private async void ConfirmImport(object sender, RoutedEventArgs e)
    {
        if (model.CanImport && MessageBox.Show(this, $"将勾选的 {model.SelectedCount} 款游戏保存到本地游戏库？",
            "确认导入", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            await model.ImportAsync();
        }
    }

    private async void LaunchGame(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameItem game })
            await model.LaunchAsync(game);
    }

    private async void OpenDetails(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameItem game } element)
            await OpenEditorAsync(new EditorModel(game, game.Directory), element, FindCard(element));
    }

    private async void AddGame(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("选择游戏安装目录") is { } directory)
        {
            var local = await Task.Run(() => metadataFiles.ReadLocal(directory));
            await OpenEditorAsync(new EditorModel(null, directory, local), sender as FrameworkElement);
        }
    }

    private void LookupPreviewMetadata(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PreviewItem { CanLookupMetadata: true } item } &&
            Lookup(item.Candidate.DisplayName) is { } result)
        {
            item.Metadata = item.Candidate.Metadata.FillMissing(result);
        }
    }

    private void LookupEditorMetadata(object sender, RoutedEventArgs e)
    {
        if (model.Editor is { } editor && model.IsAvailable && Lookup(editor.Name) is { } result)
        {
            editor.Metadata = editor.Metadata.FillMissing(result);
            editor.Error = "";
        }
    }

    private GameMetadata? Lookup(string title)
    {
        var dialog = new MetadataLookupWindow(catalog, title) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void ChooseCover(object sender, RoutedEventArgs e)
    {
        if (model.Editor is not { } editor || !model.IsAvailable)
            return;

        var picker = new OpenFileDialog
        {
            Title = "选择游戏封面",
            Filter = "JPEG 或 PNG 图片|*.jpg;*.jpeg;*.png"
        };
        if (picker.ShowDialog(this) != true)
            return;
        var cached = metadataFiles.CacheCover(picker.FileName);
        if (CoverSourceConverter.Load(cached, 180) is null)
        {
            editor.Error = "无法读取这张封面，请选择 8 MB 以内的 JPEG 或 PNG 图片。";
            return;
        }
        editor.Metadata = editor.Metadata with { CoverPath = cached };
        editor.Error = "";
    }

    private async void RelocateGame(object sender, RoutedEventArgs e)
    {
        if (model.Editor is not { } editor || ChooseFolder("选择新的安装目录", editor.Directory) is not { } path)
            return;
        if (editor.Original is null)
        {
            if (editor.Name == LocalPaths.Name(editor.Directory))
                editor.Name = LocalPaths.Name(path);
            editor.Relocate(path, null);
        }
        else
        {
            await model.RelocateAsync(editor, path);
        }
    }

    private void ChooseEntry(object sender, RoutedEventArgs e)
    {
        if (model.Editor is not { } editor)
            return;

        var picker = new OpenFileDialog { Title = "选择安装目录内的启动入口", Filter = "Windows 程序 (*.exe)|*.exe" };
        if (Directory.Exists(editor.Directory))
            picker.InitialDirectory = editor.Directory;
        if (picker.ShowDialog(this) == true)
        {
            editor.Executable = picker.FileName;
            editor.Error = "";
        }
    }

    private void ChooseWorkDirectory(object sender, RoutedEventArgs e)
    {
        if (model.Editor is { } editor && ChooseFolder("选择游戏工作目录", editor.WorkDirectory ?? editor.Directory) is { } path)
        {
            editor.WorkDirectory = path;
            editor.ConfirmWorkDirectory();
            editor.Error = "";
        }
    }

    private void DefaultWorkDirectory(object sender, RoutedEventArgs e)
    {
        if (model.Editor is { } editor)
        {
            editor.WorkDirectory = null;
            editor.ConfirmWorkDirectory();
            editor.Error = "";
        }
    }

    private void KeepWorkDirectory(object sender, RoutedEventArgs e) => model.Editor?.ConfirmWorkDirectory();
    private void AddArgument(object sender, RoutedEventArgs e) => model.Editor?.Arguments.Add(new ArgumentItem(""));

    private void RemoveArgument(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArgumentItem item })
            model.Editor?.Arguments.Remove(item);
    }

    private void MoveArgumentUp(object sender, RoutedEventArgs e) => MoveArgument(sender, -1);
    private void MoveArgumentDown(object sender, RoutedEventArgs e) => MoveArgument(sender, 1);

    private void MoveArgument(object sender, int offset)
    {
        if (sender is FrameworkElement { DataContext: ArgumentItem item } && model.Editor is { } editor)
        {
            var index = editor.Arguments.IndexOf(item);
            var target = index + offset;
            if (index >= 0 && target >= 0 && target < editor.Arguments.Count)
                editor.Arguments.Move(index, target);
        }
    }

    private async void SaveEditor(object sender, RoutedEventArgs e)
    {
        if (!transitioning && model.Editor is { } editor && await model.SaveEditorAsync(editor))
            await CloseEditorAsync();
    }

    private async void RemoveGame(object sender, RoutedEventArgs e)
    {
        if (transitioning || !model.IsAvailable || model.Editor is not { Original: { } item } editor)
            return;

        var choice = MessageBox.Show(this,
            $"要从游戏库移除「{item.DisplayName}」吗？\n{item.Directory}\n\n此操作只删除库内记录。游戏文件仍保存在原位置。",
            "移除游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes && await model.RemoveGameAsync(editor))
            await CloseEditorAsync(returnToCard: false);
    }

    private async void CancelEditor(object sender, RoutedEventArgs e) => await CloseEditorAsync();

    private string? ChooseFolder(string title, string? initialDirectory = null)
    {
        var picker = new OpenFolderDialog { Title = title };
        if (Directory.Exists(initialDirectory))
            picker.InitialDirectory = initialDirectory;
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }

    private async Task OpenEditorAsync(EditorModel editor, FrameworkElement? source, FrameworkElement? card = null)
    {
        if (model.Editor is not null || transitioning)
            return;

        transitioning = true;
        originCard = card ?? source;
        openingRect = CardBounds(originCard);
        returnFocus = source;
        model.Editor = editor;
        BasePages.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(BasePages, KeyboardNavigationMode.None);
        DetailLayer.Visibility = Visibility.Visible;
        DetailLayer.IsHitTestVisible = false;
        EditorScroll.ScrollToTop();
        DetailCover.Background = (Brush)FindResource(editor.Original?.Accent switch
        {
            1 => "BlueCover",
            2 => "GreenCover",
            _ => "PinkCover"
        });

        if (SystemParameters.ClientAreaAnimation && openingRect is { } rectangle)
        {
            Transition(rectangle, expanding: true, 315);
            await Task.Delay(315);
        }
        DetailLayer.IsHitTestVisible = true;
        transitioning = false;
        if (editor.IsExisting)
            DetailBack.Focus();
        else
            GameNameBox.Focus();
    }

    private async Task CloseEditorAsync(bool returnToCard = true)
    {
        if (model.Editor is null || model.IsBusy || transitioning)
            return;
        transitioning = true;
        DetailLayer.IsHitTestVisible = false;

        if (returnToCard && SystemParameters.ClientAreaAnimation && (CardBounds(originCard) ?? openingRect) is { } rectangle)
        {
            Transition(rectangle, expanding: false, 225);
            await Task.Delay(225);
        }
        DetailLayer.Visibility = Visibility.Collapsed;
        DetailBody.Opacity = 1;
        DetailCover.Opacity = 0;
        DetailVeil.Opacity = 1;
        DetailScale.ScaleX = DetailScale.ScaleY = 1;
        DetailOffset.X = DetailOffset.Y = 0;
        BasePages.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(BasePages, KeyboardNavigationMode.Continue);
        model.Editor = null;
        transitioning = false;
        if (returnToCard && returnFocus is FrameworkElement { IsVisible: true, IsEnabled: true } target)
            target.Focus();
        else if (model.OnLibrary)
            SearchBox.Focus();
        returnFocus = null;
        originCard = null;
        openingRect = null;
    }

    private void Transition(Rect card, bool expanding, int duration)
    {
        var width = Math.Max(Workspace.ActualWidth, 1);
        var height = Math.Max(Workspace.ActualHeight, 1);
        void Move(Animatable element, DependencyProperty property, double start, double end) =>
            Animate(element, property, expanding ? start : end, expanding ? end : start, duration);

        Move(DetailScale, ScaleTransform.ScaleXProperty, Math.Clamp(card.Width / width, .04, 1), 1);
        Move(DetailScale, ScaleTransform.ScaleYProperty, Math.Clamp(card.Height / height, .04, 1), 1);
        Move(DetailOffset, TranslateTransform.XProperty, card.X, 0);
        Move(DetailOffset, TranslateTransform.YProperty, card.Y, 0);
        Animate(DetailVeil, OpacityProperty, expanding ? 0 : 1, expanding ? 1 : 0, duration);
        Animate(DetailCover, OpacityProperty, expanding ? 1 : 0, expanding ? 0 : 1, duration);
        Animate(DetailBody, OpacityProperty, expanding ? 0 : 1, expanding ? 1 : 0,
            expanding ? 175 : 105, expanding ? 90 : 0);
    }

    private Rect? CardBounds(FrameworkElement? card)
    {
        if (card is null || !card.IsVisible || !Workspace.IsAncestorOf(card))
            return null;
        var bounds = card.TransformToAncestor(Workspace).TransformBounds(new Rect(card.RenderSize));
        bounds.Intersect(new Rect(Workspace.RenderSize));
        return bounds.IsEmpty || bounds.Width == 0 || bounds.Height == 0 ? null : bounds;
    }

    private static FrameworkElement? FindCard(DependencyObject element)
    {
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is FrameworkElement { Name: "Card" } card)
                return card;
        }
        return null;
    }

    private static DoubleAnimation Tween(double from, double to, int duration) =>
        new(from, to, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

    private static void Animate(UIElement target, DependencyProperty property, double from, double to, int duration, int delay = 0)
    {
        // An animation with a delayed begin would otherwise present the final base value before its clock starts.
        var animation = Tween(from, to, duration);
        if (delay > 0)
        {
            var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            target.SetValue(property, to);
            target.BeginAnimation(property, frames);
            return;
        }
        target.SetValue(property, to);
        target.BeginAnimation(property, animation);
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, int duration)
    {
        target.SetValue(property, to);
        target.BeginAnimation(property, Tween(from, to, duration));
    }
}
