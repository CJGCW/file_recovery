namespace FileRecoveryParser.Models;

/// <summary>
/// A single user-tagged frame: its perceptual hash plus the tag the user
/// associated with it. Used to propose tags for visually similar frames in
/// other files.
/// </summary>
public class TaggedFrame
{
    public ulong    Hash                  { get; set; }
    public string   TagName               { get; set; } = string.Empty;
    public string   SourceFile            { get; set; } = string.Empty;
    public double   SourcePositionSeconds { get; set; }
    public DateTime AddedAt               { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional 512-D MobileFaceNet embedding captured at tag-time. Present
    /// for face/region tags so cross-image identity matching can use cosine
    /// similarity rather than just perceptual-hash gradient comparison.
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Optional PNG-encoded thumbnail of the frame captured at tag-time.
    /// Travels with the tag so the management UI can still show the image
    /// even after the source file is deleted. JSON-serialised as base64.
    /// </summary>
    public byte[]? ThumbnailPng { get; set; }
}
