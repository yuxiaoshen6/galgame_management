using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GalgameManager.Application;
using static GalgameManager.Infrastructure.MetadataFiles;

namespace GalgameManager.Infrastructure;

public enum MetadataProvider { Bangumi, Vndb }

public sealed record CatalogGame(MetadataProvider Provider, string Id, string Title,
    string? OriginalTitle, string? Released, string? Summary, string? CoverUrl, string? FallbackCoverUrl);

public sealed class MetadataCatalog(HttpClient http, MetadataFiles files)
{
    public async Task<IReadOnlyList<CatalogGame>> SearchAsync(MetadataProvider provider, string title,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = provider == MetadataProvider.Bangumi
            ? new HttpRequestMessage(HttpMethod.Post, "https://api.bgm.tv/v0/search/subjects?limit=8")
            {
                Content = JsonContent.Create(new { keyword = title, sort = "match", filter = new { type = new[] { 4 } } })
            }
            : new HttpRequestMessage(HttpMethod.Post, "https://api.vndb.org/kana/vn")
            {
                Content = JsonContent.Create(new
                {
                    filters = new[] { "search", "=", title }, sort = "searchrank", results = 8,
                    fields = "title, alttitle, released, description, image.url, image.thumbnail"
                })
            };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new LibraryException("资料源请求过多，请稍后重试。");
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var bytes = await ReadLimitedAsync(body, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes);
        var key = provider == MetadataProvider.Bangumi ? "data" : "results";
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty(key, out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new LibraryException("资料源返回了无法识别的结果。");
        return entries.EnumerateArray().Take(8).Select(item => Parse(provider, item))
            .OfType<CatalogGame>().ToArray();
    }

    public async Task<string?> DownloadCoverAsync(CatalogGame game, CancellationToken cancellation)
    {
        if (game.FallbackCoverUrl is not { } fallback || game.CoverUrl == fallback)
            return await DownloadAsync(game.Provider, game.CoverUrl, 12, cancellation).ConfigureAwait(false);

        try
        {
            var cover = await DownloadAsync(game.Provider, game.CoverUrl, 7, cancellation).ConfigureAwait(false);
            if (cover is not null)
                return cover;
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested &&
            ex is (HttpRequestException or IOException or LibraryException or OperationCanceledException))
        {
            // The catalog's smaller image may still be available.
        }
        return await DownloadAsync(game.Provider, fallback, 8, cancellation).ConfigureAwait(false);
    }

    private async Task<string?> DownloadAsync(MetadataProvider provider, string? url, int seconds,
        CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !IsCoverHost(provider, uri.Host))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > CoverSizeLimit)
            throw new LibraryException("这张封面超过 8 MB，请选择本地封面。");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var bytes = await ReadLimitedAsync(stream, CoverSizeLimit, timeout.Token).ConfigureAwait(false);
        return files.CacheCoverBytes(bytes);
    }

    private static CatalogGame? Parse(MetadataProvider provider, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var identifier))
            return null;

        if (provider == MetadataProvider.Bangumi)
        {
            if (identifier.ValueKind != JsonValueKind.Number || GetString(item, "name") is not { } name)
                return null;
            var localized = GetString(item, "name_cn");
            var hasImages = item.TryGetProperty("images", out var images);
            var small = hasImages ? GetString(images, "common") : null;
            var cover = hasImages ? GetString(images, "large") ?? small : null;
            if (cover?.EndsWith("/no_icon_subject.png", StringComparison.OrdinalIgnoreCase) == true)
                cover = small = null;
            return new CatalogGame(provider, identifier.GetRawText(),
                string.IsNullOrWhiteSpace(localized) ? name : localized,
                !string.IsNullOrWhiteSpace(localized) && localized != name ? name : null,
                GetString(item, "date"), GetString(item, "summary"), cover, small);
        }

        if (identifier.ValueKind != JsonValueKind.String || GetString(item, "title") is not { } title)
            return null;
        var hasImage = item.TryGetProperty("image", out var image);
        var thumbnail = hasImage ? GetString(image, "thumbnail") : null;
        return new CatalogGame(provider, identifier.GetString()!, title, GetString(item, "alttitle"),
            GetString(item, "released"), GetString(item, "description"),
            hasImage ? GetString(image, "url") ?? thumbnail : null, thumbnail);
    }

    private static bool IsCoverHost(MetadataProvider provider, string host) =>
        provider == MetadataProvider.Bangumi ? host.EndsWith(".bgm.tv", StringComparison.OrdinalIgnoreCase)
            : host.EndsWith(".vndb.org", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancellation)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (count == 0)
                return result.ToArray();
            if (result.Length + count > limit)
                throw new LibraryException("资料文件过大。");
            result.Write(buffer, 0, count);
        }
    }
}
