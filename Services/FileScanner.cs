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
    private static long _idCounter;

    // Folders we always skip regardless of user config — Windows protected
    // / per-user areas that we can't usefully scan into anyway.
    private static readonly HashSet<string> SystemSkippedFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "System Volume Information",
            "$RECYCLE.BIN", "$Recycle.Bin",
            "Config.Msi",
            "Recovery",
        };

    private readonly FileTypeDetector _detector = new();
    private readonly int _parallelism;
    private readonly HashSet<string> _userSkippedFolders;

    public FileScanner(int? parallelism = null, IEnumerable<string>? extraSkippedFolderNames = null)
    {
        _parallelism = parallelism ?? Math.Max(2, Environment.ProcessorCount);
        _userSkippedFolders = extraSkippedFolderNames is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(extraSkippedFolderNames, StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<FileRecord> ScanAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var files = EnumerateFilesSkippingFolders(rootPath);

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

    // Manual directory walk so we can prune subtrees by folder name. Each
    // directory's files stream out first, then subdirectories get pushed onto
    // the stack unless their name is in the system or user skip set.
    // SafeEnumerate* wraps the OS enumerator so a single bad subdir (perms,
    // IO mid-enumeration) ends that directory's iteration without killing
    // the whole scan, while keeping enumeration lazy — no per-directory list
    // materialisation, which is what the previous version did and what made
    // scans noticeably slower on deep trees.
    private IEnumerable<string> EnumerateFilesSkippingFolders(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            foreach (var f in SafeEnumerateFiles(dir))
                yield return f;

            foreach (var sd in SafeEnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sd);
                if (SystemSkippedFolders.Contains(name)) continue;
                if (_userSkippedFolders.Contains(name))  continue;
                stack.Push(sd);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        IEnumerator<string> e;
        try { e = Directory.EnumerateFiles(dir).GetEnumerator(); }
        catch { yield break; }
        using (e)
        {
            while (true)
            {
                bool moved;
                try { moved = e.MoveNext(); }
                catch { yield break; }
                if (!moved) yield break;
                yield return e.Current;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        IEnumerator<string> e;
        try { e = Directory.EnumerateDirectories(dir).GetEnumerator(); }
        catch { yield break; }
        using (e)
        {
            while (true)
            {
                bool moved;
                try { moved = e.MoveNext(); }
                catch { yield break; }
                if (!moved) yield break;
                yield return e.Current;
            }
        }
    }

    private FileRecord? ProcessFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return null;

            var detected = _detector.Detect(filePath);
            if (detected is null) return null;

            var cat = detected.Value.Category;
            // Use the inferred extension (handles PhotoRec underscore names)
            // so the filter UI and per-extension behaviour treat e.g.
            // "f0002227_memdiag_exe" exactly like "memdiag.exe".
            var ext = FileTypeDetector.GetEffectiveExtension(filePath);

            // Compute once — used for both VideoInfo and Duration
            VideoInfo? videoInfo = cat == FileCategory.Video
                ? VideoMetadataReader.Read(filePath) : null;

            return new FileRecord
            {
                Id               = Interlocked.Increment(ref _idCounter),
                FullPath         = filePath,
                FileName         = info.Name,
                Extension        = ext,
                DetectedMimeType = detected.Value.MimeType,
                Category         = cat,
                FileSizeBytes    = info.Length,
                LastModified     = info.LastWriteTimeUtc,
                ScannedAt        = DateTime.UtcNow,
                DocumentTitle    = cat == FileCategory.Document ? DocumentMetadataReader.ReadTitle(filePath) : null,
                DocumentContent  = cat == FileCategory.Document ? DocumentMetadataReader.Read(filePath)     : null,
                VideoInfo        = videoInfo,
                Duration         = cat == FileCategory.Video ? videoInfo?.Duration
                                 : cat == FileCategory.Audio ? MediaDurationReader.Read(filePath)
                                 : null,
                ImageGroup       = cat == FileCategory.Image
                                       ? ImageClassifier.Classify(filePath, info.Extension)
                                       : ImageSubcategory.None
            };
        }
        catch
        {
            return null;
        }
    }
}
