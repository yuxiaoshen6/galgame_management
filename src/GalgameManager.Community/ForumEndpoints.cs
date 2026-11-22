using System.Security.Claims;
using GalgameManager.Application.Community;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Community;

internal static class ForumEndpoints
{
    public static void MapForum(this WebApplication app)
    {
        var forum = app.MapGroup("/api/forum").RequireAuthorization();
        forum.MapGet("/topics", ListTopics);
        forum.MapGet("/topics/{id:long}", ReadTopic);
        forum.MapPost("/topics", CreateTopic);
        forum.MapPost("/topics/{id:long}/replies", AddReply);
    }

    private static async Task<IResult> ListTopics(string? q, int? page, CommunityDatabase database, CancellationToken token)
    {
        var number = page ?? 1;
        var query = q?.Trim();
        if (!ValidPage(number) || query?.Length > CommunityLimits.TopicTitle)
            return AuthEndpoints.Error("搜索词或页码无效。");

        var topics = database.Topics.AsNoTracking();
        if (!string.IsNullOrEmpty(query))
            topics = topics.Where(item => item.Title.Contains(query));
        var items = await topics.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id)
            .Skip(Offset(number)).Take(CommunityLimits.PageSize + 1)
            .Select(item => new ForumTopic(item.Id, item.Title, item.Author.Nickname, item.CreatedUtc, item.Replies.Count))
            .ToListAsync(token);
        return Results.Ok(new ForumPage<ForumTopic>(items.Take(CommunityLimits.PageSize)
            .Select(item => item with { CreatedUtc = Utc(item.CreatedUtc) }).ToArray(),
            number, items.Count > CommunityLimits.PageSize));
    }

    private static async Task<IResult> ReadTopic(long id, int? page, CommunityDatabase database, CancellationToken token)
    {
        var number = page ?? 1;
        if (!ValidPage(number))
            return AuthEndpoints.Error("页码无效。");
        var match = await database.Topics.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new
            {
                item.Body, Summary = new ForumTopic(item.Id, item.Title, item.Author.Nickname, item.CreatedUtc, item.Replies.Count)
            }).SingleOrDefaultAsync(token);
        if (match is null)
            return AuthEndpoints.Error("帖子不存在。", StatusCodes.Status404NotFound);

        var replies = await database.Replies.AsNoTracking().Where(item => item.TopicId == id)
            .OrderBy(item => item.Id).Skip(Offset(number)).Take(CommunityLimits.PageSize + 1)
            .Select(item => new ForumReply(item.Id, item.Body, item.Author.Nickname, item.CreatedUtc))
            .ToListAsync(token);
        var replyPage = new ForumPage<ForumReply>(replies.Take(CommunityLimits.PageSize)
            .Select(item => item with { CreatedUtc = Utc(item.CreatedUtc) }).ToArray(),
            number, replies.Count > CommunityLimits.PageSize);
        return Results.Ok(new ForumTopicDetails(match.Summary with { CreatedUtc = Utc(match.Summary.CreatedUtc) },
            match.Body, replyPage));
    }

    private static async Task<IResult> CreateTopic(ForumTopicInput input, ClaimsPrincipal principal,
        CommunityDatabase database, TimeProvider time, CancellationToken token)
    {
        var title = input.Title?.Trim();
        var body = input.Body?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > CommunityLimits.TopicTitle || title.Any(char.IsControl))
            return AuthEndpoints.Error("帖子标题需为 1 到 120 个字符。");
        if (!ValidBody(body, CommunityLimits.TopicBody))
            return AuthEndpoints.Error("帖子正文需为 1 到 12000 个字符。");
        var topic = new Topic
        {
            Title = title, Body = body!, CreatedUtc = MySqlUtcNow(time),
            AuthorId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!
        };
        database.Topics.Add(topic);
        await database.SaveChangesAsync(token);
        return Results.Created($"/api/forum/topics/{topic.Id}",
            new ForumTopic(topic.Id, title, principal.Identity!.Name!, topic.CreatedUtc, 0));
    }

    private static async Task<IResult> AddReply(long id, ForumReplyInput input, ClaimsPrincipal principal,
        CommunityDatabase database, TimeProvider time, CancellationToken token)
    {
        var body = input.Body?.Trim();
        if (!ValidBody(body, CommunityLimits.ReplyBody))
            return AuthEndpoints.Error("回复需为 1 到 6000 个字符。");
        if (!await database.Topics.AnyAsync(item => item.Id == id, token))
            return AuthEndpoints.Error("帖子不存在。", StatusCodes.Status404NotFound);
        var reply = new Reply
        {
            TopicId = id, Body = body!, CreatedUtc = MySqlUtcNow(time),
            AuthorId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!
        };
        database.Replies.Add(reply);
        await database.SaveChangesAsync(token);
        return Results.Created($"/api/forum/topics/{id}",
            new ForumReply(reply.Id, reply.Body, principal.Identity!.Name!, reply.CreatedUtc));
    }

    private static int Offset(int page) => (page - 1) * CommunityLimits.PageSize;
    private static bool ValidPage(int page) => page is >= 1 and <= CommunityLimits.MaxPage;
    private static DateTime Utc(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc);
    private static DateTime MySqlUtcNow(TimeProvider time)
    {
        // MySQL datetime(6) stores microseconds, so creation and subsequent reads return the same timestamp.
        var now = time.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }
    private static bool ValidBody(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit &&
        value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');
}
