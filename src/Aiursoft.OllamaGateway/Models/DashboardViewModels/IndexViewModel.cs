using System.Diagnostics.CodeAnalysis;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.DashboardViewModels;

[ExcludeFromCodeCoverage]

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Dashboard";
    }

    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int TotalApiKeys { get; set; }
    public int TotalOllamaProviders { get; set; }
    public int TotalVirtualModels { get; set; }
    public int ChatModelsCount { get; set; }
    public int EmbeddingModelsCount { get; set; }

    public List<ProviderStats> ProviderStats { get; set; } = [];
    public List<TopApiKeyStats> TopApiKeys { get; set; } = [];
    public List<TopModelStats> TopModels { get; set; } = [];
    public List<RecentUserStats> RecentUsers { get; set; } = [];
    public List<ActiveModelInfo> ActiveRequests { get; set; } = [];
}

[ExcludeFromCodeCoverage]

public class ProviderStats
{
    public string Name { get; set; } = string.Empty;
    public int ModelCount { get; set; }
}

[ExcludeFromCodeCoverage]

public class TopApiKeyStats
{
    public string Name { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty; // This field was already present in my last update.
    public long UsageCount { get; set; }
    public DateTime? LastUsed { get; set; }
}

[ExcludeFromCodeCoverage]

public class TopModelStats
{
    public string ModelName { get; set; } = string.Empty;
    public long UsageCount { get; set; }
}

[ExcludeFromCodeCoverage]

public class RecentUserStats
{
    public string Email { get; set; } = string.Empty;
    public DateTime CreationTime { get; set; }
}

[ExcludeFromCodeCoverage]
public class ActiveModelInfo
{
    public string ModelName { get; set; } = string.Empty;
    public int ActiveCount { get; set; }
    public string LastQuestion { get; set; } = string.Empty;
    public DateTime LastStartedAt { get; set; }
}
