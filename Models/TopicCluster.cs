namespace FileRecoveryParser.Models;

public class TopicCluster
{
    public List<string> Keywords        { get; set; } = [];
    public string?      PreferredFolder { get; set; }
    public int          FileCount       { get; set; }
}
