using System.Collections.Concurrent;
using System.IO;
using DeniaMemoryForensics.Models;

namespace DeniaMemoryForensics.Services;

public sealed class ImageCarver
{
    private const int ChunkSize = 8 * 1024 * 1024;
    private const int CarrySize = 32;

    public async Task<IReadOnlyList<CarvedFile>> CarveAsync(
        string dumpPath,
        string outputDirectory,
        CarveOptions options,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(dumpPath))
        {
            throw new FileNotFoundException("Memory dump was not found.", dumpPath);
        }

        Directory.CreateDirectory(outputDirectory);
        var selected = ParseExtensions(options.Extensions);
        var results = new ConcurrentBag<CarvedFile>();
        var seenOffsets = new HashSet<long>();

        await using var stream = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
        var fileLength = stream.Length;
        var chunk = new byte[ChunkSize];
        var carry = Array.Empty<byte>();
        long absolute = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            var buffer = new byte[carry.Length + read];
            Buffer.BlockCopy(carry, 0, buffer, 0, carry.Length);
            Buffer.BlockCopy(chunk, 0, buffer, carry.Length, read);
            var bufferStart = absolute - carry.Length;

            for (var i = 0; i < buffer.Length - 12; i++)
            {
                if (options.Limit > 0 && results.Count >= options.Limit)
                {
                    break;
                }

                var offset = bufferStart + i;
                if (offset < 0 || !seenOffsets.Add(offset))
                {
                    continue;
                }

                var match = Match(buffer, i, selected);
                if (match is null)
                {
                    continue;
                }

                var carved = await ExtractAsync(dumpPath, outputDirectory, offset, fileLength, match, options, cancellationToken);
                if (carved is not null)
                {
                    results.Add(carved);
                    onProgress?.Invoke($"[{results.Count}] {carved.Type} offset=0x{carved.Offset:X} size={FormatBytes(carved.Size)} -> {Path.GetFileName(carved.Path)}");
                }
            }

            absolute += read;
            var carryLength = Math.Min(CarrySize, buffer.Length);
            carry = new byte[carryLength];
            Buffer.BlockCopy(buffer, buffer.Length - carryLength, carry, 0, carryLength);

            var percent = absolute * 100.0 / Math.Max(1, fileLength);
            onProgress?.Invoke($"Scan {percent:0.00}% ({FormatBytes(absolute)} / {FormatBytes(fileLength)})");

            if (options.Limit > 0 && results.Count >= options.Limit)
            {
                break;
            }
        }

        var ordered = results.OrderBy(item => item.Offset).ToList();
        WriteManifest(outputDirectory, ordered);
        return ordered;
    }

    private static ImageMatch? Match(byte[] data, int index, HashSet<string> selected)
    {
        if (selected.Contains("jpg") && data[index] == 0xFF && data[index + 1] == 0xD8 && data[index + 2] == 0xFF)
        {
            return new ImageMatch("jpg", ImageLengthMode.Footer, new byte[] { 0xFF, 0xD9 });
        }

        if (selected.Contains("png") &&
            data[index] == 0x89 && data[index + 1] == 0x50 && data[index + 2] == 0x4E && data[index + 3] == 0x47 &&
            data[index + 4] == 0x0D && data[index + 5] == 0x0A && data[index + 6] == 0x1A && data[index + 7] == 0x0A)
        {
            return new ImageMatch("png", ImageLengthMode.Footer, new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 });
        }

        if (selected.Contains("gif") &&
            data[index] == 0x47 && data[index + 1] == 0x49 && data[index + 2] == 0x46 &&
            data[index + 3] == 0x38 && (data[index + 4] == 0x37 || data[index + 4] == 0x39) && data[index + 5] == 0x61)
        {
            return new ImageMatch("gif", ImageLengthMode.Footer, new byte[] { 0x3B });
        }

        if (selected.Contains("bmp") && data[index] == 0x42 && data[index + 1] == 0x4D)
        {
            return new ImageMatch("bmp", ImageLengthMode.SizeAtOffset, SizeOffset: 2);
        }

        if (selected.Contains("ico") && data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1 && data[index + 3] == 0)
        {
            return new ImageMatch("ico", ImageLengthMode.IcoDirectory);
        }

        if (selected.Contains("webp") &&
            data[index] == 0x52 && data[index + 1] == 0x49 && data[index + 2] == 0x46 && data[index + 3] == 0x46 &&
            data[index + 8] == 0x57 && data[index + 9] == 0x45 && data[index + 10] == 0x42 && data[index + 11] == 0x50)
        {
            return new ImageMatch("webp", ImageLengthMode.RiffSize);
        }

        if (selected.Contains("tif") || selected.Contains("tiff"))
        {
            var intel = data[index] == 0x49 && data[index + 1] == 0x49 && data[index + 2] == 0x2A && data[index + 3] == 0;
            var motorola = data[index] == 0x4D && data[index + 1] == 0x4D && data[index + 2] == 0 && data[index + 3] == 0x2A;
            if (intel || motorola)
            {
                return new ImageMatch("tiff", ImageLengthMode.MaxOnly);
            }
        }

        return null;
    }

    private static async Task<CarvedFile?> ExtractAsync(
        string sourcePath,
        string outputDirectory,
        long offset,
        long sourceLength,
        ImageMatch match,
        CarveOptions options,
        CancellationToken cancellationToken)
    {
        var max = Math.Min(options.MaxBytes, sourceLength - offset);
        if (max < options.MinBytes)
        {
            return null;
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        source.Seek(offset, SeekOrigin.Begin);

        var length = await DetermineLengthAsync(source, match, max, cancellationToken);
        if (length < options.MinBytes || length > options.MaxBytes)
        {
            return null;
        }

        source.Seek(offset, SeekOrigin.Begin);
        var name = $"{match.Extension}_{offset:X12}.{match.Extension}";
        var path = Path.Combine(outputDirectory, name);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024);
        await CopyExactAsync(source, output, length, cancellationToken);

        var valid = !options.Validate || BasicValidate(path, match.Extension);
        if (options.Validate && !valid)
        {
            File.Delete(path);
            return null;
        }

        return new CarvedFile(path, match.Extension, offset, length, valid);
    }

    private static async Task<long> DetermineLengthAsync(FileStream source, ImageMatch match, long maxBytes, CancellationToken cancellationToken)
    {
        return match.Mode switch
        {
            ImageLengthMode.Footer => await FindFooterLengthAsync(source, match.Footer!, maxBytes, cancellationToken),
            ImageLengthMode.SizeAtOffset => await ReadUInt32LengthAsync(source, match.SizeOffset, maxBytes, cancellationToken),
            ImageLengthMode.RiffSize => await ReadUInt32LengthAsync(source, 4, maxBytes, cancellationToken, addBytes: 8),
            ImageLengthMode.IcoDirectory => await ReadIcoLengthAsync(source, maxBytes, cancellationToken),
            ImageLengthMode.MaxOnly => Math.Min(maxBytes, 8 * 1024 * 1024),
            _ => 0
        };
    }

    private static async Task<long> FindFooterLengthAsync(FileStream source, byte[] footer, long maxBytes, CancellationToken cancellationToken)
    {
        var window = new Queue<byte>(footer.Length);
        var buffer = new byte[64 * 1024];
        long total = 0;

        while (total < maxBytes)
        {
            var readTarget = (int)Math.Min(buffer.Length, maxBytes - total);
            var read = await source.ReadAsync(buffer.AsMemory(0, readTarget), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                window.Enqueue(buffer[i]);
                if (window.Count > footer.Length)
                {
                    window.Dequeue();
                }

                total++;
                if (window.Count == footer.Length && window.SequenceEqual(footer))
                {
                    return total;
                }
            }
        }

        return 0;
    }

    private static async Task<long> ReadUInt32LengthAsync(
        FileStream source,
        int offset,
        long maxBytes,
        CancellationToken cancellationToken,
        int addBytes = 0)
    {
        var header = new byte[offset + 4];
        var read = await source.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        if (read != header.Length)
        {
            return 0;
        }

        var length = BitConverter.ToUInt32(header, offset) + addBytes;
        return length > 0 && length <= maxBytes ? length : 0;
    }

    private static async Task<long> ReadIcoLengthAsync(FileStream source, long maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[6];
        if (await source.ReadAsync(header.AsMemory(0, header.Length), cancellationToken) != header.Length)
        {
            return 0;
        }

        var count = BitConverter.ToUInt16(header, 4);
        if (count == 0 || count > 64)
        {
            return 0;
        }

        var entries = new byte[count * 16];
        if (await source.ReadAsync(entries.AsMemory(0, entries.Length), cancellationToken) != entries.Length)
        {
            return 0;
        }

        long maxEnd = 6 + entries.Length;
        for (var i = 0; i < count; i++)
        {
            var baseIndex = i * 16;
            var size = BitConverter.ToUInt32(entries, baseIndex + 8);
            var imageOffset = BitConverter.ToUInt32(entries, baseIndex + 12);
            maxEnd = Math.Max(maxEnd, imageOffset + size);
        }

        return maxEnd <= maxBytes ? maxEnd : 0;
    }

    private static async Task CopyExactAsync(Stream source, Stream output, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            var readTarget = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, readTarget), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static bool BasicValidate(string path, string extension)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length < 8)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            _ = file.Read(header);
            return extension switch
            {
                "jpg" => header[0] == 0xFF && header[1] == 0xD8,
                "png" => header[0] == 0x89 && header[1] == 0x50,
                "gif" => header[0] == 0x47 && header[1] == 0x49,
                "bmp" => header[0] == 0x42 && header[1] == 0x4D,
                "ico" => header[0] == 0 && header[1] == 0 && header[2] == 1,
                "webp" => header[0] == 0x52 && header[8] == 0x57,
                "tiff" => (header[0] == 0x49 && header[1] == 0x49) || (header[0] == 0x4D && header[1] == 0x4D),
                _ => true
            };
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<string> ParseExtensions(string extensions)
    {
        var defaults = new[] { "jpg", "jpeg", "png", "gif", "bmp", "ico", "webp", "tif", "tiff" };
        var items = string.IsNullOrWhiteSpace(extensions)
            ? defaults
            : extensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var normalized = items.Select(item => item.Trim().TrimStart('.').ToLowerInvariant()).ToHashSet();
        if (normalized.Contains("jpeg"))
        {
            normalized.Add("jpg");
        }

        return normalized;
    }

    private static void WriteManifest(string outputDirectory, IEnumerable<CarvedFile> files)
    {
        var csv = Path.Combine(outputDirectory, "manifest.csv");
        using var writer = new StreamWriter(csv);
        writer.WriteLine("path,type,offset,size,valid");
        foreach (var file in files)
        {
            writer.WriteLine($"\"{file.Path.Replace("\"", "\"\"")}\",{file.Type},0x{file.Offset:X},{file.Size},{file.Valid}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private sealed record ImageMatch(
        string Extension,
        ImageLengthMode Mode,
        byte[]? Footer = null,
        int SizeOffset = 0);

    private enum ImageLengthMode
    {
        Footer,
        SizeAtOffset,
        RiffSize,
        IcoDirectory,
        MaxOnly
    }
}
