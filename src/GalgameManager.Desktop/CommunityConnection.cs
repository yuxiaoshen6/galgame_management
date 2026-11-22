using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using GalgameManager.Infrastructure;

namespace GalgameManager.Desktop;

internal sealed record CommunityConnection(string? ApiBaseUrl, string? CertificateSha256)
{
    public static CommunityClient Open()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "community.json");
        CommunityConnection? settings = null;
        try
        {
            if (File.Exists(settingsPath))
                settings = JsonSerializer.Deserialize<CommunityConnection>(File.ReadAllText(settingsPath),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        var configOverride = Environment.GetEnvironmentVariable("GALGAMEMANAGER_COMMUNITY_URL");
        var candidate = configOverride ?? settings?.ApiBaseUrl;
        Uri? address = null;
        if (Uri.TryCreate(candidate?.TrimEnd('/') + "/", UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
            address = uri;

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var fingerprint = configOverride is null ? settings?.CertificateSha256 : null;
        if (!string.IsNullOrWhiteSpace(fingerprint) && address is { Scheme: "https" })
        {
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                certificate is not null && request.RequestUri?.Host == address.Host &&
                (errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors) &&
                certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), fingerprint.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }
        return new CommunityClient(new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(12) });
    }
}
