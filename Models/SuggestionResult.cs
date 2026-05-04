namespace FileRecoveryParser.Models;

public record SuggestionResult(
    string? SuggestedName,
    string? SuggestedFolder,
    float   Confidence,
    string  Source          // "face" | "doc-name" | "doc-topic"
);
