namespace Aiursoft.OllamaGateway.Models.Configuration;

public class ClickhouseOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}
