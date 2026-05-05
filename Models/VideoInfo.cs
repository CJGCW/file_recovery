namespace FileRecoveryParser.Models;

public record VideoInfo(
    TimeSpan? Duration,
    string?   EmbeddedTitle,
    uint?     Year,
    uint?     FrameWidth,
    uint?     FrameHeight,
    string?   ParsedShowName,
    int?      ParsedSeason,
    int?      ParsedEpisode,
    int?      ParsedYear)
{
    public string? Resolution =>
        FrameWidth is > 0 && FrameHeight is > 0
            ? $"{FrameWidth}×{FrameHeight}"
            : null;

    // Best-guess title: embedded metadata wins, then filename parse
    public string? BestTitle =>
        !string.IsNullOrWhiteSpace(EmbeddedTitle) ? EmbeddedTitle :
        !string.IsNullOrWhiteSpace(ParsedShowName) ? ParsedShowName : null;

    public string? EpisodeLabel =>
        ParsedSeason is not null && ParsedEpisode is not null
            ? $"S{ParsedSeason:D2}E{ParsedEpisode:D2}"
            : null;
}
