using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Configuration;

[ExcludeFromCodeCoverage]
public class LocalSettings
{
    public required bool AllowRegister { get; init; } = true;

    public required bool AllowWeakPassword { get; init; } = true;
}
