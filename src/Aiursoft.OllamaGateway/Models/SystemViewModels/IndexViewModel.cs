using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.SystemViewModels;

public class MigrationEntry
{
    public required string Id { get; init; }
    public string Name => Id.Length > 15 ? Id[15..] : Id;
    public DateTime? AppliedAt => Id.Length >= 14 && DateTime.TryParseExact(
        Id[..14],
        "yyyyMMddHHmmss",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal,
        out var parsed) ? parsed.ToUniversalTime() : null;
}

[ExcludeFromCodeCoverage]
public class TableCountEntry
{
    public required string Name { get; init; }
    public long Rows { get; init; }
}

[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "System Info";
    }

    public List<TableCountEntry> TableCounts { get; init; } = [];
    public List<MigrationEntry> AppliedMigrations { get; init; } = [];
    public int TotalDefinedMigrations { get; init; }
    public List<string> PendingMigrations { get; init; } = [];
}
