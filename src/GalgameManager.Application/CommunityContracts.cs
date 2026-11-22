namespace GalgameManager.Application.Community;

public sealed record Registration(string Email, string Nickname, string Password);
public sealed record Login(string Email, string Password);
public sealed record Account(string Id, string Email, string Nickname);
public sealed record AccountSession(string Token, DateTime ExpiresUtc, Account Account);
public sealed record ForumTopicInput(string Title, string Body);
public sealed record ForumReplyInput(string Body);
public sealed record ForumTopic(long Id, string Title, string Author, DateTime CreatedUtc, int ReplyCount);
public sealed record ForumReply(long Id, string Body, string Author, DateTime CreatedUtc);
public sealed record ForumTopicDetails(ForumTopic Topic, string Body, ForumPage<ForumReply> Replies);
public sealed record ForumPage<T>(IReadOnlyList<T> Items, int Page, bool HasMore);
public sealed record CommunityError(string Message);

public static class CommunityLimits
{
    public const int Email = 254;
    public const int Nickname = 40;
    public const int PasswordMin = 10;
    public const int PasswordMax = 128;
    public const int TopicTitle = 120;
    public const int TopicBody = 12000;
    public const int ReplyBody = 6000;
    public const int PageSize = 25;
    public const int MaxPage = 10000;
}
