namespace FileRecoveryParser.Models;

public class PersonRecord
{
    public Guid          Id              { get; set; } = Guid.NewGuid();
    public string?       Name            { get; set; }
    public string?       PreferredFolder { get; set; }
    public List<float[]> Embeddings      { get; set; } = [];
    public DateTime      LastSeen        { get; set; } = DateTime.UtcNow;
}
