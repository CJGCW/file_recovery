using FileRecoveryParser.Models;

namespace FileRecoveryParser.Data;

/// <summary>
/// Maps file extensions to MIME types and broad categories.
/// Files without a recognised extension are skipped by the scanner.
/// </summary>
public static class ExtensionMap
{
    public static readonly IReadOnlyDictionary<string, (string Mime, FileCategory Category)> Entries =
        new Dictionary<string, (string, FileCategory)>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Images ───────────────────────────────────────────────────────
            { ".jpg",    ("image/jpeg",                     FileCategory.Image) },
            { ".jpeg",   ("image/jpeg",                     FileCategory.Image) },
            { ".jpe",    ("image/jpeg",                     FileCategory.Image) },
            { ".png",    ("image/png",                      FileCategory.Image) },
            { ".gif",    ("image/gif",                      FileCategory.Image) },
            { ".bmp",    ("image/bmp",                      FileCategory.Image) },
            { ".tif",    ("image/tiff",                     FileCategory.Image) },
            { ".tiff",   ("image/tiff",                     FileCategory.Image) },
            { ".webp",   ("image/webp",                     FileCategory.Image) },
            { ".svg",    ("image/svg+xml",                  FileCategory.Image) },
            { ".ico",    ("image/x-icon",                   FileCategory.Image) },
            { ".heic",   ("image/heic",                     FileCategory.Image) },
            { ".heif",   ("image/heif",                     FileCategory.Image) },
            { ".avif",   ("image/avif",                     FileCategory.Image) },
            { ".jxl",    ("image/jxl",                      FileCategory.Image) },
            // RAW camera formats
            { ".raw",    ("image/x-raw",                    FileCategory.Image) },
            { ".cr2",    ("image/x-canon-cr2",              FileCategory.Image) },
            { ".cr3",    ("image/x-canon-cr3",              FileCategory.Image) },
            { ".nef",    ("image/x-nikon-nef",              FileCategory.Image) },
            { ".nrw",    ("image/x-nikon-nrw",              FileCategory.Image) },
            { ".arw",    ("image/x-sony-arw",               FileCategory.Image) },
            { ".orf",    ("image/x-olympus-orf",            FileCategory.Image) },
            { ".rw2",    ("image/x-panasonic-rw2",          FileCategory.Image) },
            { ".raf",    ("image/x-fuji-raf",               FileCategory.Image) },
            { ".dng",    ("image/x-adobe-dng",              FileCategory.Image) },
            { ".pef",    ("image/x-pentax-pef",             FileCategory.Image) },
            { ".srw",    ("image/x-samsung-srw",            FileCategory.Image) },
            { ".x3f",    ("image/x-sigma-x3f",              FileCategory.Image) },
            // Editing formats
            { ".psd",    ("image/vnd.adobe.photoshop",      FileCategory.Image) },
            { ".psb",    ("image/vnd.adobe.photoshop",      FileCategory.Image) },
            { ".xcf",    ("image/x-xcf",                    FileCategory.Image) },
            { ".ai",     ("application/postscript",         FileCategory.Image) },
            { ".eps",    ("application/postscript",         FileCategory.Image) },
            { ".jp2",    ("image/jp2",                      FileCategory.Image) },

            // ── Video ────────────────────────────────────────────────────────
            { ".mp4",    ("video/mp4",                      FileCategory.Video) },
            { ".m4v",    ("video/x-m4v",                    FileCategory.Video) },
            { ".mkv",    ("video/x-matroska",               FileCategory.Video) },
            { ".avi",    ("video/x-msvideo",                FileCategory.Video) },
            { ".mov",    ("video/quicktime",                FileCategory.Video) },
            { ".wmv",    ("video/x-ms-wmv",                 FileCategory.Video) },
            { ".flv",    ("video/x-flv",                    FileCategory.Video) },
            { ".webm",   ("video/webm",                     FileCategory.Video) },
            { ".mpeg",   ("video/mpeg",                     FileCategory.Video) },
            { ".mpg",    ("video/mpeg",                     FileCategory.Video) },
            { ".m2v",    ("video/mpeg",                     FileCategory.Video) },
            { ".3gp",    ("video/3gpp",                     FileCategory.Video) },
            { ".3g2",    ("video/3gpp2",                    FileCategory.Video) },
            { ".ts",     ("video/mp2t",                     FileCategory.Video) },
            { ".mts",    ("video/mp2t",                     FileCategory.Video) },
            { ".m2ts",   ("video/mp2t",                     FileCategory.Video) },
            { ".vob",    ("video/dvd",                      FileCategory.Video) },
            { ".ogv",    ("video/ogg",                      FileCategory.Video) },
            { ".rmvb",   ("application/vnd.rn-realmedia",   FileCategory.Video) },
            { ".divx",   ("video/x-divx",                   FileCategory.Video) },
            { ".f4v",    ("video/mp4",                      FileCategory.Video) },

            // ── Audio ────────────────────────────────────────────────────────
            { ".mp3",    ("audio/mpeg",                     FileCategory.Audio) },
            { ".wav",    ("audio/wav",                      FileCategory.Audio) },
            { ".flac",   ("audio/flac",                     FileCategory.Audio) },
            { ".ogg",    ("audio/ogg",                      FileCategory.Audio) },
            { ".oga",    ("audio/ogg",                      FileCategory.Audio) },
            { ".opus",   ("audio/opus",                     FileCategory.Audio) },
            { ".m4a",    ("audio/mp4",                      FileCategory.Audio) },
            { ".m4b",    ("audio/mp4",                      FileCategory.Audio) }, // audiobook
            { ".wma",    ("audio/x-ms-wma",                 FileCategory.Audio) },
            { ".aac",    ("audio/aac",                      FileCategory.Audio) },
            { ".aiff",   ("audio/aiff",                     FileCategory.Audio) },
            { ".aif",    ("audio/aiff",                     FileCategory.Audio) },
            { ".aifc",   ("audio/aiff",                     FileCategory.Audio) },
            { ".ape",    ("audio/x-ape",                    FileCategory.Audio) },
            { ".wv",     ("audio/x-wavpack",                FileCategory.Audio) },
            { ".mka",    ("audio/x-matroska",               FileCategory.Audio) },
            { ".amr",    ("audio/amr",                      FileCategory.Audio) },
            { ".mid",    ("audio/midi",                     FileCategory.Audio) },
            { ".midi",   ("audio/midi",                     FileCategory.Audio) },
            { ".ra",     ("audio/x-realaudio",              FileCategory.Audio) },
            { ".caf",    ("audio/x-caf",                    FileCategory.Audio) }, // Apple Core Audio

            // ── Documents ────────────────────────────────────────────────────
            { ".pdf",    ("application/pdf",                FileCategory.Document) },
            { ".doc",    ("application/msword",             FileCategory.Document) },
            { ".docx",   ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileCategory.Document) },
            { ".xls",    ("application/vnd.ms-excel",       FileCategory.Document) },
            { ".xlsx",   ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileCategory.Document) },
            { ".ppt",    ("application/vnd.ms-powerpoint",  FileCategory.Document) },
            { ".pptx",   ("application/vnd.openxmlformats-officedocument.presentationml.presentation", FileCategory.Document) },
            { ".odt",    ("application/vnd.oasis.opendocument.text", FileCategory.Document) },
            { ".ods",    ("application/vnd.oasis.opendocument.spreadsheet", FileCategory.Document) },
            { ".odp",    ("application/vnd.oasis.opendocument.presentation", FileCategory.Document) },
            { ".txt",    ("text/plain",                     FileCategory.Document) },
            { ".rtf",    ("application/rtf",                FileCategory.Document) },
            { ".csv",    ("text/csv",                       FileCategory.Document) },
            { ".xml",    ("application/xml",                FileCategory.Document) },
            { ".html",   ("text/html",                      FileCategory.Document) },
            { ".htm",    ("text/html",                      FileCategory.Document) },
            { ".md",     ("text/markdown",                  FileCategory.Document) },
            { ".epub",   ("application/epub+zip",           FileCategory.Document) },

            // ── Archives ─────────────────────────────────────────────────────
            { ".zip",    ("application/zip",                FileCategory.Archive) },
            { ".rar",    ("application/x-rar-compressed",   FileCategory.Archive) },
            { ".7z",     ("application/x-7z-compressed",    FileCategory.Archive) },
            { ".gz",     ("application/gzip",               FileCategory.Archive) },
            { ".tar",    ("application/x-tar",              FileCategory.Archive) },
            { ".bz2",    ("application/x-bzip2",            FileCategory.Archive) },
            { ".xz",     ("application/x-xz",              FileCategory.Archive) },
            { ".iso",    ("application/x-iso9660-image",    FileCategory.Archive) },
            { ".dmg",    ("application/x-apple-diskimage",  FileCategory.Archive) },

            // ── Code ─────────────────────────────────────────────────────────
            { ".cs",     ("text/x-csharp",                  FileCategory.Code) },
            { ".js",     ("text/javascript",                FileCategory.Code) },
            { ".py",     ("text/x-python",                  FileCategory.Code) },
            { ".java",   ("text/x-java",                    FileCategory.Code) },
            { ".cpp",    ("text/x-c++src",                  FileCategory.Code) },
            { ".c",      ("text/x-csrc",                    FileCategory.Code) },
            { ".h",      ("text/x-chdr",                    FileCategory.Code) },
            { ".go",     ("text/x-go",                      FileCategory.Code) },
            { ".rs",     ("text/x-rustsrc",                 FileCategory.Code) },
            { ".rb",     ("text/x-ruby",                    FileCategory.Code) },
            { ".php",    ("text/x-php",                     FileCategory.Code) },
            { ".swift",  ("text/x-swift",                   FileCategory.Code) },
            { ".kt",     ("text/x-kotlin",                  FileCategory.Code) },
            { ".json",   ("application/json",               FileCategory.Code) },
            { ".yaml",   ("text/yaml",                      FileCategory.Code) },
            { ".yml",    ("text/yaml",                      FileCategory.Code) },
            { ".toml",   ("text/toml",                      FileCategory.Code) },
            { ".sql",    ("text/x-sql",                     FileCategory.Code) },
            { ".sh",     ("text/x-shellscript",             FileCategory.Code) },

            // ── Fonts ─────────────────────────────────────────────────────────
            { ".ttf",    ("font/ttf",                       FileCategory.Font) },
            { ".otf",    ("font/otf",                       FileCategory.Font) },
            { ".woff",   ("font/woff",                      FileCategory.Font) },
            { ".woff2",  ("font/woff2",                     FileCategory.Font) },

            // ── Databases ─────────────────────────────────────────────────────
            { ".db",     ("application/x-sqlite3",          FileCategory.Database) },
            { ".sqlite", ("application/x-sqlite3",          FileCategory.Database) },
            { ".sqlite3",("application/x-sqlite3",          FileCategory.Database) },

            // ── Executables ───────────────────────────────────────────────────
            { ".exe",    ("application/x-msdownload",       FileCategory.Executable) },
            { ".dll",    ("application/x-msdownload",       FileCategory.Executable) },
            { ".so",     ("application/x-elf",              FileCategory.Executable) },
            { ".dylib",  ("application/x-mach-binary",      FileCategory.Executable) },
        };
}
