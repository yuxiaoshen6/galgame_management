using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GalgameManager.Application.Community;

namespace GalgameManager.Infrastructure;

public sealed class CommunityException(string message) : Exception(message);

public sealed class CommunityClient(HttpClient http) : IDisposable
{
    private AccountSession? session;
    public Account? Account => session?.Account;
    public event Action? AccountChanged;

    public async Task RegisterAsync(Registration request, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/auth/register", request, protectedRoute: false, token);
    }

    public async Task SignInAsync(Login request, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/auth/login", request, protectedRoute: false, token);
        var signedIn = await ReadAsync<AccountSession>(response, token);
        token.ThrowIfCancellationRequested();
        session = signedIn;
        AccountChanged?.Invoke();
    }

    public async Task SignOutAsync()
    {
        if (session is not { } old)
            return;
        ClearSession(old);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", old.Token);
        try
        {
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
                throw new CommunityException("已退出本机登录。服务器暂时无法结束该会话。");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new CommunityException("已退出本机登录。服务器暂时无法结束该会话。");
        }
    }

    public Task<ForumPage<ForumTopic>> ListAsync(string query, int page, CancellationToken token) =>
        ForumAsync<ForumPage<ForumTopic>>(HttpMethod.Get, $"api/forum/topics?page={page}&q={Uri.EscapeDataString(query)}", null, token);

    public Task<ForumTopicDetails> ReadAsync(long id, int page, CancellationToken token) =>
        ForumAsync<ForumTopicDetails>(HttpMethod.Get, $"api/forum/topics/{id}?page={page}", null, token);

    public Task<ForumTopic> CreateAsync(ForumTopicInput input, CancellationToken token) =>
        ForumAsync<ForumTopic>(HttpMethod.Post, "api/forum/topics", input, token);

    public Task<ForumReply> ReplyAsync(long id, ForumReplyInput input, CancellationToken token) =>
        ForumAsync<ForumReply>(HttpMethod.Post, $"api/forum/topics/{id}/replies", input, token);

    private async Task<T> ForumAsync<T>(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var response = await SendAsync(method, path, body, protectedRoute: true, token);
        return await ReadAsync<T>(response, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? input, bool protectedRoute,
        CancellationToken token)
    {
        if (http.BaseAddress is null)
            throw new CommunityException("论坛服务器地址尚未配置。");
        var active = session;
        if (protectedRoute && (active is null || active.ExpiresUtc <= DateTime.UtcNow))
        {
            ClearSession(active);
            throw new CommunityException("登录已过期，请重新登录。");
        }

        using var request = new HttpRequestMessage(method, path);
        if (input is not null)
            request.Content = JsonContent.Create(input);
        if (protectedRoute)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active!.Token);
        try
        {
            var response = await http.SendAsync(request, token);
            if (response.IsSuccessStatusCode)
                return response;

            using (response)
            {
                if (protectedRoute && response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    ClearSession(active);
                    throw new CommunityException("登录已过期，请重新登录。");
                }

                var message = response.Content.Headers.ContentType?.MediaType == "application/json"
                    ? (await response.Content.ReadFromJsonAsync<CommunityError>(cancellationToken: token))?.Message
                    : null;
                throw new CommunityException(message ?? "服务器暂时无法完成请求。");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or
            OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new CommunityException("无法连接论坛服务器，请检查网络后重试。");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
                ?? throw new CommunityException("服务器返回了空结果。");
        }
        catch (JsonException)
        {
            throw new CommunityException("服务器返回了无法识别的资料。");
        }
    }

    private void ClearSession(AccountSession? expected)
    {
        if (expected is null || !ReferenceEquals(session, expected))
            return;
        session = null;
        AccountChanged?.Invoke();
    }

    public void Dispose() => http.Dispose();
}
