namespace Aiursoft.OllamaGateway.Entities;

public class WarmupModel
{
    public required string Name { get; set; }
    public bool IsEmbedding { get; set; }
    public int? NumCtx { get; set; }
    public int? TimeoutSeconds { get; set; }
}
