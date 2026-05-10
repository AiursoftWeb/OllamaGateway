using System.Text.Json.Serialization;

namespace Aiursoft.OllamaGateway.Models.AnthropicViewModels;

public class AnthropicMessageRequest
{
    [JsonPropertyName("model")]
    [Newtonsoft.Json.JsonProperty("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    [Newtonsoft.Json.JsonProperty("messages")]
    // ReSharper disable once CollectionNeverUpdated.Global
    public List<AnthropicMessage> Messages { get; set; } = new();

    [JsonPropertyName("max_tokens")]
    [Newtonsoft.Json.JsonProperty("max_tokens")]
    public int? MaxTokens { get; set; } = 4096;

    [JsonPropertyName("stream")]
    [Newtonsoft.Json.JsonProperty("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("system")]
    [Newtonsoft.Json.JsonProperty("system")]
    public object? System { get; set; }

    [JsonPropertyName("temperature")]
    [Newtonsoft.Json.JsonProperty("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [Newtonsoft.Json.JsonProperty("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("tools")]
    [Newtonsoft.Json.JsonProperty("tools")]
    public List<AnthropicTool>? Tools { get; set; }
}

public class AnthropicMessage
{
    [JsonPropertyName("role")]
    [Newtonsoft.Json.JsonProperty("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [Newtonsoft.Json.JsonProperty("content")]
    public object? Content { get; set; }
}

public class AnthropicTool
{
    [JsonPropertyName("name")]
    [Newtonsoft.Json.JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [Newtonsoft.Json.JsonProperty("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    [Newtonsoft.Json.JsonProperty("input_schema")]
    public object? InputSchema { get; set; }
}
