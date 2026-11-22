using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GalgameManager.Community;

public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, CommunityDatabase database, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CommunitySession";
    public const string SessionClaim = "community-session";

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        if (header.Parameter is not { Length: 64 } token || !token.All(Uri.IsHexDigit))
            return AuthenticateResult.Fail("Invalid session.");

        var hash = HashToken(token);
        var now = time.GetUtcNow().UtcDateTime;
        var user = await database.Sessions.AsNoTracking()
            .Where(item => item.TokenHash == hash && item.ExpiresUtc > now)
            .Select(item => item.User)
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("Invalid session.");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Nickname),
            new Claim(ClaimTypes.Email, user.Email!),
            new Claim(SessionClaim, hash)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
