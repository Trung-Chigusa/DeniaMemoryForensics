using System.IO;

namespace DeniaMemoryForensics.Services;

public sealed class VolatilityRunner
{
    private readonly ExternalCommandRunner _runner = new();

    public bool IsConfigured(string enginePath) => !string.IsNullOrWhiteSpace(enginePath) && File.Exists(enginePath);

    public Task RunBattleCommandAsync(
        string enginePath,
        string dumpPath,
        string commandLine,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(enginePath))
        {
            throw new InvalidOperationException("Volatility/Battle engine is not configured.");
        }

        if (string.IsNullOrWhiteSpace(dumpPath) || !File.Exists(dumpPath))
        {
            throw new InvalidOperationException("Select a memory dump first.");
        }

        var args = new List<string> { "--cli", "--quiet", "-f", dumpPath };
        args.AddRange(SplitCommandLine(commandLine));
        return _runner.RunAsync(enginePath, args, onOutput, cancellationToken);
    }

    public Task RunStatusAsync(string enginePath, string dumpPath, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        if (!IsConfigured(enginePath))
        {
            throw new InvalidOperationException("Volatility/Battle engine is not configured.");
        }

        var args = new List<string> { "--cli", "--quiet" };
        if (!string.IsNullOrWhiteSpace(dumpPath) && File.Exists(dumpPath))
        {
            args.Add("-f");
            args.Add(dumpPath);
        }

        args.Add("status");
        return _runner.RunAsync(enginePath, args, onOutput, cancellationToken);
    }

    private static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                Flush();
                continue;
            }

            current.Add(ch);
        }

        Flush();
        return result;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            result.Add(new string(current.ToArray()));
            current.Clear();
        }
    }
}
