using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Aiursoft.OllamaGateway.Models;

public class OllamaRequestModel
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;
    
    [JsonPropertyName("messages")]
    [SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
    public List<OllamaMessage>? Messages { get; set; }
    
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
    
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
    
    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; set; }
    
    [JsonPropertyName("options")]
    public OllamaRequestOptions? Options { get; set; }
    
    [JsonPropertyName("think")]
    public bool? Think { get; set; }
    
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }
    
    [JsonPropertyName("system")]
    public string? System { get; set; }
    
    [JsonPropertyName("template")]
    public string? Template { get; set; }
    
    [JsonPropertyName("context")]
    public string? Context { get; set; }
    
    [JsonPropertyName("format")]
    public string? Format { get; set; }
    
    [JsonPropertyName("raw")]
    public bool? Raw { get; set; }
}

public class OllamaMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
    
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
    
    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }
}

public class OllamaRequestOptions
{
    [JsonPropertyName("num_ctx")]
    public int? NumCtx { get; set; }
    
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
    
    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }
    
    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }
    
    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; set; }
    
    [JsonPropertyName("repeat_penalty")]
    public float? RepeatPenalty { get; set; }
}
