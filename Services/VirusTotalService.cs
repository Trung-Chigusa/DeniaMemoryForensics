using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DeniaMemoryForensics.Models;

namespace DeniaMemoryForensics.Services;

public sealed class VirusTotalService
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("https://www.virustotal.com/api/v3/")
    };

    public async Task<VirusTotalResult> CheckFileAsync(string path, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("VirusTotal API key is required.");
        }

        var sha256 = await ComputeSha256Async(path, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"files/{sha256}");
        request.Headers.Add("x-apikey", apiKey);

        using var response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new VirusTotalResult(path, sha256, "unknown", 0, 0, 0, 0, "Hash not found in VirusTotal.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new VirusTotalResult(path, sha256, "error", 0, 0, 0, 0, $"{(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var stats = doc.RootElement
            .GetProperty("data")
            .GetProperty("attributes")
            .GetProperty("last_analysis_stats");

        var malicious = GetInt(stats, "malicious");
        var suspicious = GetInt(stats, "suspicious");
        var harmless = GetInt(stats, "harmless");
        var undetected = GetInt(stats, "undetected");
        var verdict = malicious > 0 ? "malicious" : suspicious > 0 ? "suspicious" : "clean";
        var message = $"{malicious} malicious, {suspicious} suspicious, {harmless} harmless, {undetected} undetected";

        return new VirusTotalResult(path, sha256, verdict, malicious, suspicious, harmless, undetected, message);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static IReadOnlyList<string> CollectFiles(string target, bool executablesOnly, int limit)
    {
        if (File.Exists(target))
        {
            return new[] { target };
        }

        if (!Directory.Exists(target))
        {
            return Array.Empty<string>();
        }

        var executableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".sys", ".scr", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jar", ".dmp"
        };

        return Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(path => !executablesOnly || executableExtensions.Contains(Path.GetExtension(path)))
            .Take(limit <= 0 ? 500 : limit)
            .ToList();
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }
}
