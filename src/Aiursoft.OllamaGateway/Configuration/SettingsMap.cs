using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Configuration;

public class SettingsMap
{
    public const string ProjectName = "ProjectName";
    public const string BrandName = "BrandName";
    public const string BrandHomeUrl = "BrandHomeUrl";
    public const string ProjectLogo = "ProjectLogo";
    public const string AllowUserAdjustNickname = "Allow_User_Adjust_Nickname";
    public const string Icp = "Icp";
    public const string DefaultChatModel = "DefaultChatModel";
    public const string DefaultEmbeddingModel = "DefaultEmbeddingModel";
    public const string RequestTimeoutInMinutes = "RequestTimeoutInMinutes";
    public const string AllowAnonymousApiCall = "AllowAnonymousApiCall";

    public class FakeLocalizer
    {
        public string this[string name] => name;
    }

    private static readonly FakeLocalizer Localizer = new();

    public static readonly List<GlobalSettingDefinition> Definitions = new()
    {
        new GlobalSettingDefinition
        {
            Key = ProjectName,
            Name = Localizer["Project Name"],
            Description = Localizer["The name of the project displayed in the frontend."],
            Type = SettingType.Text,
            DefaultValue = "Aiursoft Template"
        },
        new GlobalSettingDefinition
        {
            Key = BrandName,
            Name = Localizer["Brand Name"],
            Description = Localizer["The brand name displayed in the footer."],
            Type = SettingType.Text,
            DefaultValue = "Aiursoft"
        },
        new GlobalSettingDefinition
        {
            Key = BrandHomeUrl,
            Name = Localizer["Brand Home URL"],
            Description = Localizer[" The link to the brand's home page."],
            Type = SettingType.Text,
            DefaultValue = "https://www.aiursoft.com/"
        },
        new GlobalSettingDefinition
        {
            Key = ProjectLogo,
            Name = Localizer["Project Logo"],
            Description = Localizer["The logo of the project displayed in the navbar and footer. Support jpg, png, svg."],
            Type = SettingType.File,
            DefaultValue = "",
            Subfolder = "project-logo",
            AllowedExtensions = "jpg png svg",
            MaxSizeInMb = 5
        },
        new GlobalSettingDefinition
        {
            Key = AllowUserAdjustNickname,
            Name = Localizer["Allow User Adjust Nickname"],
            Description = Localizer["Allow users to adjust their nickname in the profile management page."],
            Type = SettingType.Bool,
            DefaultValue = "True"
        },
        new GlobalSettingDefinition
        {
            Key = Icp,
            Name = Localizer["ICP Number"],
            Description = Localizer["The ICP license number for China mainland users. Leave empty to hide."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = DefaultChatModel,
            Name = Localizer["Default Chat Model"],
            Description = Localizer["The default virtual model to use for chat requests when no model is specified."],
            Type = SettingType.Text,
            DefaultValue = "llama3.2"
        },
        new GlobalSettingDefinition
        {
            Key = DefaultEmbeddingModel,
            Name = Localizer["Default Embedding Model"],
            Description = Localizer["The default virtual model to use for embedding requests when no model is specified."],
            Type = SettingType.Text,
            DefaultValue = "nomic-embed-text"
        },
        new GlobalSettingDefinition
        {
            Key = RequestTimeoutInMinutes,
            Name = Localizer["Request Timeout (Minutes)"],
            Description = Localizer["The maximum time in minutes to wait for a response from the underlying Ollama server."],
            Type = SettingType.Number,
            DefaultValue = "10"
        },
        new GlobalSettingDefinition
        {
            Key = AllowAnonymousApiCall,
            Name = Localizer["Allow Anonymous API Call"],
            Description = Localizer["Allow anyone to call the API without a Bearer token or authentication."],
            Type = SettingType.Bool,
            DefaultValue = "False"
        }
    };
}
