using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GalgameManager.Application.Community;
using GalgameManager.Infrastructure;

namespace GalgameManager.Desktop;

public partial class ForumWindow : Window
{
    private readonly CommunityClient client;
    private readonly CancellationTokenSource lifetime = new();
    private string query = "";
    private int topicPage = 1;
    private ForumTopicDetails? current;
    private bool busy;

    public ForumWindow(CommunityClient client)
    {
        this.client = client;
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync(token => LoadTopicsAsync("", 1, token));
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private async void Search(object sender, RoutedEventArgs e) =>
        await RunAsync(token => LoadTopicsAsync(SearchBox.Text.Trim(), 1, token));

    private void SearchOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Search(sender, e);
        }
    }

    private async void PreviousTopicPage(object sender, RoutedEventArgs e) =>
        await RunAsync(token => LoadTopicsAsync(query, topicPage - 1, token));

    private async void NextTopicPage(object sender, RoutedEventArgs e) =>
        await RunAsync(token => LoadTopicsAsync(query, topicPage + 1, token));

    private async Task LoadTopicsAsync(string search, int page, CancellationToken token)
    {
        var result = await client.ListAsync(search, page, token);
        token.ThrowIfCancellationRequested();
        query = search;
        topicPage = page;
        Topics.ItemsSource = result.Items;
        PreviousTopics.IsEnabled = result.Page > 1;
        NextTopics.IsEnabled = result.HasMore && result.Page < CommunityLimits.MaxPage;
        TopicPageLabel.Text = $"第 {result.Page} 页";
        Status.Text = result.Items.Count == 0 ? (search.Length == 0 ? "论坛还没有帖子。" : "未找到匹配的帖子。") : "";
        ClearTopic();
    }

    private async void SelectTopic(object sender, SelectionChangedEventArgs e)
    {
        if (Topics.SelectedItem is ForumTopic topic)
        {
            if (current?.Topic.Id != topic.Id)
                ClearTopic();
            await RunAsync(token => LoadTopicAsync(topic.Id, 1, token));
            if (current is null)
                Topics.SelectedItem = null;
        }
    }

    private async void PreviousReplyPage(object sender, RoutedEventArgs e)
    {
        if (current is { } topic)
            await RunAsync(token => LoadTopicAsync(topic.Topic.Id, topic.Replies.Page - 1, token));
    }

    private async void NextReplyPage(object sender, RoutedEventArgs e)
    {
        if (current is { } topic)
            await RunAsync(token => LoadTopicAsync(topic.Topic.Id, topic.Replies.Page + 1, token));
    }

    private async Task LoadTopicAsync(long id, int page, CancellationToken token)
    {
        var result = await client.ReadAsync(id, page, token);
        token.ThrowIfCancellationRequested();
        if (current?.Topic.Id != id)
            ReplyBox.Clear();
        current = result;
        ThreadTitle.Text = result.Topic.Title;
        ThreadAuthor.Text = $"{result.Topic.Author} · {result.Topic.CreatedUtc.ToLocalTime():g}";
        ThreadBody.Text = result.Body;
        Replies.ItemsSource = result.Replies.Items;
        ReplyPageLabel.Text = $"第 {page} 页 · {result.Topic.ReplyCount} 条";
        PreviousReplies.IsEnabled = page > 1;
        NextReplies.IsEnabled = result.Replies.HasMore && page < CommunityLimits.MaxPage;
        EmptyTopic.Visibility = Visibility.Collapsed;
        TopicDetail.Visibility = Visibility.Visible;
        ThreadScroll.ScrollToTop();
    }

    private void ClearTopic()
    {
        current = null;
        TopicDetail.Visibility = Visibility.Collapsed;
        EmptyTopic.Visibility = Visibility.Visible;
        ReplyBox.Clear();
    }

    private void NewTopic(object sender, RoutedEventArgs e)
    {
        Compose.Visibility = Visibility.Visible;
        TitleBox.Focus();
    }

    private void CancelTopic(object sender, RoutedEventArgs e) => Compose.Visibility = Visibility.Collapsed;

    private async void PublishTopic(object sender, RoutedEventArgs e)
    {
        var input = new ForumTopicInput(TitleBox.Text.Trim(), BodyBox.Text.Trim());
        if (input.Title.Length == 0 || input.Body.Length == 0)
        {
            Status.Text = "请填写帖子标题和正文。";
            return;
        }
        await RunAsync(async token =>
        {
            var topic = await client.CreateAsync(input, token);
            token.ThrowIfCancellationRequested();
            TitleBox.Clear();
            BodyBox.Clear();
            Compose.Visibility = Visibility.Collapsed;
            SearchBox.Clear();
            await LoadTopicsAsync("", 1, token);
            await LoadTopicAsync(topic.Id, 1, token);
            Status.Text = "帖子已发布。";
        });
    }

    private async void SendReply(object sender, RoutedEventArgs e)
    {
        var body = ReplyBox.Text.Trim();
        if (current is not { } topic || body.Length == 0)
        {
            Status.Text = "请填写回复正文。";
            return;
        }
        await RunAsync(async token =>
        {
            await client.ReplyAsync(topic.Topic.Id, new ForumReplyInput(body), token);
            token.ThrowIfCancellationRequested();
            ReplyBox.Clear();
            var page = Math.Min(CommunityLimits.MaxPage, topic.Topic.ReplyCount / CommunityLimits.PageSize + 1);
            await LoadTopicAsync(topic.Topic.Id, page, token);
            ThreadScroll.ScrollToEnd();
            Status.Text = "回复已发布。";
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy)
            return;
        var token = lifetime.Token;
        busy = true;
        ForumContent.IsEnabled = NewTopicButton.IsEnabled = false;
        Status.Text = "正在连接论坛…";
        try
        {
            await action(token);
            if (Status.Text == "正在连接论坛…")
                Status.Text = "";
        }
        catch (CommunityException ex)
        {
            if (IsVisible)
                Status.Text = ex.Message;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            busy = false;
            if (IsVisible)
                ForumContent.IsEnabled = NewTopicButton.IsEnabled = true;
        }
    }
}
