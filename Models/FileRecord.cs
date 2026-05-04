namespace FileRecoveryParser.Models;

public class FileRecord
{
    public long Id { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string DetectedMimeType { get; set; } = string.Empty;
    public FileCategory Category { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime? LastModified { get; set; }
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
}
