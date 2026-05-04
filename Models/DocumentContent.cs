namespace FileRecoveryParser.Models;

public record DocumentContent(
    string?       Title,
    string?       HeaderText,
    string?       Author,
    List<string>  Keywords
);
