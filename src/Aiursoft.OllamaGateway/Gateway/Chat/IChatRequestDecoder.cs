namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatRequestDecoder
{
    ProtocolDialect Dialect { get; }

    DecodedChatRequest Decode(string body);
}
