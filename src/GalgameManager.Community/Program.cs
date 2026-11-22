using System.Net;
using System.Threading.RateLimiting;
using GalgameManager.Application.Community;
using GalgameManager.Community;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("ForumDatabase");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("Configure ConnectionStrings__ForumDatabase for the community service.");

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 128 * 1024;
});
builder.Services.AddDbContext<CommunityDatabase>(options => options.UseMySQL(connection));
builder.Services.AddIdentityCore<CommunityUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.User.AllowedUserNameCharacters = ""; // User names are validated email addresses.
    options.Password.RequiredLength = CommunityLimits.PasswordMin;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
}).AddEntityFrameworkStores<CommunityDatabase>().AddSignInManager();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthentication(SessionAuthentication.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthentication>(SessionAuthentication.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
        await context.HttpContext.Response.WriteAsJsonAsync(new CommunityError("操作太频繁，请稍后重试。"), token);
    options.AddPolicy("register", context => Limit(context, 8, TimeSpan.FromHours(1)));
    options.AddPolicy("login", context => Limit(context, 30, TimeSpan.FromMinutes(1)));
});

var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new CommunityError("服务器暂时无法完成请求，请稍后重试。"));
}));
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps && context.Connection.RemoteIpAddress is { } address &&
        !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuth();
app.MapForum();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<CommunityDatabase>();
    await database.Database.EnsureCreatedAsync();
    await database.Users.AnyAsync();
    await database.Sessions.AnyAsync();
    await database.Topics.AnyAsync();
    await database.Replies.AnyAsync();
}
await app.RunAsync();

static RateLimitPartition<string> Limit(HttpContext context, int permits, TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });
