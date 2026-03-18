using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.DashboardViewModels;

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
}

public class ProviderStats
{
    public string Name { get; set; } = string.Empty;
    public int ModelCount { get; set; }
}

public class TopApiKeyStats
{
    public string Name { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty; // This field was already present in my last update.
    public long UsageCount { get; set; }
    public DateTime? LastUsed { get; set; }
}

public class TopModelStats
{
    public string ModelName { get; set; } = string.Empty;
    public long UsageCount { get; set; }
}

public class RecentUserStats
{
    public string Email { get; set; } = string.Empty;
    public DateTime CreationTime { get; set; }
}
