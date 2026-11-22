using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using GalgameManager.Application.Community;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Community;

internal static class AuthEndpoints
{
    private static readonly EmailAddressAttribute EmailValidator = new();
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(12);

    public static void MapAuth(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").RequireAuthorization();
        group.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting("register");
        group.MapPost("/login", SignIn).AllowAnonymous().RequireRateLimiting("login");
        group.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new Account(
            principal.FindFirstValue(ClaimTypes.NameIdentifier)!, principal.FindFirstValue(ClaimTypes.Email)!,
            principal.Identity!.Name!)));
        group.MapPost("/logout", SignOut);
    }

    private static async Task<IResult> Register(Registration request, UserManager<CommunityUser> users)
    {
        var email = request.Email?.Trim();
        var nickname = request.Nickname?.Trim();
        if (!ValidEmail(email))
            return Error("请输入有效的邮箱地址。");
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > CommunityLimits.Nickname || nickname.Any(char.IsControl))
            return Error("昵称需为 1 到 40 个字符。");
        if (request.Password is not { Length: >= CommunityLimits.PasswordMin and <= CommunityLimits.PasswordMax })
            return Error("密码长度需为 10 到 128 个字符。");

        var user = new CommunityUser { Email = email, UserName = email, Nickname = nickname, LockoutEnabled = true };
        try
        {
            var result = await users.CreateAsync(user, request.Password);
            if (result.Succeeded)
                return Results.StatusCode(StatusCodes.Status201Created);
            if (result.Errors.Any(error => error.Code is "DuplicateUserName" or "DuplicateEmail"))
                return Error("此邮箱已注册，请直接登录。", StatusCodes.Status409Conflict);
            return Error("注册资料不符合要求，请核对邮箱、昵称和密码。");
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is MySql.Data.MySqlClient.MySqlException { Number: 1062 })
        {
            return Error("此邮箱已注册，请直接登录。", StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> SignIn(Login request, UserManager<CommunityUser> users,
        SignInManager<CommunityUser> signIn, CommunityDatabase database, TimeProvider time, CancellationToken cancellation)
    {
        var email = request.Email?.Trim();
        if (!ValidEmail(email) || request.Password is not { Length: > 0 and <= CommunityLimits.PasswordMax })
            return Error("邮箱或密码不正确。", StatusCodes.Status401Unauthorized);

        var user = await users.FindByEmailAsync(email!);
        if (user is null || !(await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true)).Succeeded)
            return Error("邮箱或密码不正确，或尝试次数过多。", StatusCodes.Status401Unauthorized);

        var now = time.GetUtcNow().UtcDateTime;
        await database.Sessions.Where(item => item.ExpiresUtc <= now).ExecuteDeleteAsync(cancellation);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = now.Add(SessionDuration);
        database.Sessions.Add(new CommunitySession
        {
            TokenHash = SessionAuthentication.HashToken(token), UserId = user.Id, ExpiresUtc = expires
        });
        await database.SaveChangesAsync(cancellation);
        return Results.Ok(new AccountSession(token, expires, new Account(user.Id, user.Email!, user.Nickname)));
    }

    private static async Task<IResult> SignOut(ClaimsPrincipal principal, CommunityDatabase database, CancellationToken cancellation)
    {
        var hash = principal.FindFirstValue(SessionAuthentication.SessionClaim);
        await database.Sessions.Where(item => item.TokenHash == hash).ExecuteDeleteAsync(cancellation);
        return Results.NoContent();
    }

    private static bool ValidEmail(string? value) =>
        value is { Length: > 0 and <= CommunityLimits.Email } && EmailValidator.IsValid(value);

    internal static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new CommunityError(message), statusCode: status);
}
