using System.Text;
using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Services.Proxy.Translators;

public static class OpenAIRequestTranslator
{
    public static JsonObject TranslateChatRequest(JsonObject clientJson, string underlyingModelName, bool think)
    {
        var isStream = clientJson["stream"]?.GetValue<bool>() ?? false;
        
        var ollamaRequest = new JsonObject
        {
            ["model"] = underlyingModelName,
            ["stream"] = isStream
        };

        if (clientJson["tools"] != null) ollamaRequest["tools"] = clientJson["tools"]!.DeepClone();
        if (clientJson["tool_choice"] != null) ollamaRequest["tool_choice"] = clientJson["tool_choice"]!.DeepClone();

        var messagesArray = clientJson["messages"]?.AsArray();
        if (messagesArray != null)
        {
            var translatedMessages = new JsonArray();
            foreach (var msgNode in messagesArray)
            {
                if (msgNode == null) continue;
                var newMsg = new JsonObject();
                newMsg["role"] = msgNode["role"]?.ToString();

                var contentNode = msgNode["content"];
                if (contentNode is JsonArray contentArray)
                {
                    var textBuilder = new StringBuilder();
                    var imagesArray = new JsonArray();
                    foreach (var item in contentArray)
                    {
                        var type = item?["type"]?.ToString();
                        if (type == "text")
                        {
                            var textVal = item?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(textVal)) textBuilder.Append(textVal);
                        }
                        else if (type == "image_url")
                        {
                            var url = item?["image_url"]?["url"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                var base64Data = url.Contains(',') ? url.Split(',')[1] : url;
                                imagesArray.Add(base64Data);
                            }
                        }
                    }
                    newMsg["content"] = textBuilder.ToString();
                    if (imagesArray.Count > 0) newMsg["images"] = imagesArray;
                }
                else
                {
                    newMsg["content"] = contentNode?.ToString() ?? string.Empty;
                }

                var tcs = msgNode["tool_calls"]?.AsArray();
                if (tcs != null && tcs.Count > 0)
                {
                    var ollamaTcs = new JsonArray();
                    foreach (var tc in tcs)
                    {
                        var oTc = new JsonObject();
                        if (tc?["id"] != null) oTc["id"] = tc["id"]!.DeepClone();
                        if (tc?["type"] != null) oTc["type"] = tc["type"]!.DeepClone();
                        var funcNode = tc?["function"];
                        if (funcNode != null)
                        {
                            var oFunc = new JsonObject();
                            oFunc["name"] = funcNode["name"]?.ToString();
                            var argsStr = funcNode["arguments"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(argsStr))
                            {
                                try { oFunc["arguments"] = JsonNode.Parse(argsStr); }
                                catch { oFunc["arguments"] = new JsonObject(); }
                            }
                            oTc["function"] = oFunc;
                        }
                        ollamaTcs.Add(oTc);
                    }
                    newMsg["tool_calls"] = ollamaTcs;
                }
                if (msgNode["tool_call_id"] != null) newMsg["tool_call_id"] = msgNode["tool_call_id"]?.ToString();
                translatedMessages.Add(newMsg);
            }
            ollamaRequest["messages"] = translatedMessages;
        }

        var options = new JsonObject();
        if (clientJson["temperature"] != null) options["temperature"] = clientJson["temperature"]!.DeepClone();
        if (clientJson["top_p"] != null) options["top_p"] = clientJson["top_p"]!.DeepClone();
        if (clientJson["max_tokens"] != null) options["num_predict"] = clientJson["max_tokens"]!.DeepClone();

        if (options.Count > 0) ollamaRequest["options"] = options;
        if (think) ollamaRequest["think"] = true;

        return ollamaRequest;
    }
}
