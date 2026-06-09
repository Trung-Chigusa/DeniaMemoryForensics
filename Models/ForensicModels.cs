namespace DeniaMemoryForensics.Models;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record CarveOptions(
    string Extensions,
    long MinBytes,
    long MaxBytes,
    int Limit,
    bool Validate);

public sealed record CarvedFile(
    string Path,
    string Type,
    long Offset,
    long Size,
    bool Valid);

public sealed record VirusTotalResult(
    string Path,
    string Sha256,
    string Verdict,
    int Malicious,
    int Suspicious,
    int Harmless,
    int Undetected,
    string Message);

public sealed class DeniaSettings
{
    public string VolatilityEnginePath { get; set; } = "";
    public string VirusTotalApiKey { get; set; } = "";
    public string LastDumpPath { get; set; } = "";
    public string OutputRoot { get; set; } = "";
}
