using System.IO;
using System.Runtime.CompilerServices;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

/// <summary>
/// Walks a directory tree and yields FileRecord objects via IAsyncEnumerable.
/// No database dependency — results are streamed directly to the caller.
/// </summary>
public class FileScanner
{
    private readonly FileTypeDetector _detector = new();
    private readonly int _parallelism;

    public FileScanner(int? parallelism = null)
    {
        _parallelism = parallelism ?? Math.Max(2, Environment.ProcessorCount);
    }

    public async IAsyncEnumerable<FileRecord> ScanAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories);

        var channel = System.Threading.Channels.Channel.CreateBounded<FileRecord>(
            new System.Threading.Channels.BoundedChannelOptions(2000)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode     = System.Threading.Channels.BoundedChannelFullMode.Wait
            });

        var writeTask = Task.Run(async () =>
        {
            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = _parallelism,
                CancellationToken      = ct
            };

            try
            {
                await Parallel.ForEachAsync(files, opts, async (path, innerCt) =>
                {
                    var record = ProcessFile(path);
                    if (record is not null)
                        await channel.Writer.WriteAsync(record, innerCt);
                });
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        await foreach (var record in channel.Reader.ReadAllAsync(ct))
            yield return record;

        await writeTask;
    }

    private FileRecord? ProcessFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            var detected = _detector.Detect(filePath);
            if (detected is null) return null;

            return new FileRecord
            {
                FullPath         = filePath,
                FileName         = info.Name,
                Extension        = info.Extension.ToLowerInvariant(),
                DetectedMimeType = detected.Value.MimeType,
                Category         = detected.Value.Category,
                FileSizeBytes    = info.Length,
                LastModified     = info.LastWriteTimeUtc,
                ScannedAt        = DateTime.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }
}
