using System.Security.Cryptography;
using System.Text.Json;
using GalgameManager.Application;

namespace GalgameManager.Infrastructure;

public sealed class MetadataFiles(string dataDirectory) : IGameMetadataFiles
{
    internal const int CoverSizeLimit = 8 * 1024 * 1024;
    private const int TextSizeLimit = 512 * 1024;
    private static readonly string[] CoverNames = ["cover.jpg", "cover.png", "cover.jpeg"];
    private readonly string coverDirectory = Path.Combine(dataDirectory, "covers");

    public GameMetadata ReadLocal(string installDirectory)
    {
        string? cover = null;
        Span<byte> header = stackalloc byte[8];
        foreach (var name in CoverNames)
        {
            var path = Path.Combine(installDirectory, name);
            if (!IsLocalFile(path))
                continue;

            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length <= CoverSizeLimit && stream.ReadAtLeast(header, header.Length, false) == header.Length &&
                    CoverExtension(header) is not null)
                {
                    cover = path;
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var json = ReadSmallText(Path.Combine(installDirectory, "metadata.json"));
        string? summary = null;
        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var metadata = document.RootElement;
                summary = GetString(metadata, "summary");
                if (string.IsNullOrWhiteSpace(summary))
                    summary = GetString(metadata, "description");
            }
            catch (JsonException) { }
        }

        if (string.IsNullOrWhiteSpace(summary))
            summary = ReadSmallText(Path.Combine(installDirectory, "description.txt"));
        return new GameMetadata(summary, cover);
    }

    public string? CacheCover(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length <= 0 || stream.Length > CoverSizeLimit)
                return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return CacheCoverBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal string? CacheCoverBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > CoverSizeLimit || CoverExtension(bytes) is not { } extension)
            return null;

        var name = Convert.ToHexString(SHA256.HashData(bytes)) + extension;
        var destination = Path.Combine(coverDirectory, name);
        Directory.CreateDirectory(coverDirectory);
        if (File.Exists(destination))
            return destination;

        var temp = Path.Combine(coverDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = File.Create(temp))
                file.Write(bytes);
            try { File.Move(temp, destination); }
            catch (IOException) when (File.Exists(destination)) { }
            return destination;
        }
        finally { File.Delete(temp); }
    }

    internal static string? GetString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadSmallText(string path)
    {
        if (!IsLocalFile(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > TextSizeLimit)
                return null;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsLocalFile(string path)
    {
        if (!File.Exists(path))
            return false;
        try { return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? CoverExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            return ".png";
        return bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ? ".jpg" : null;
    }
}
