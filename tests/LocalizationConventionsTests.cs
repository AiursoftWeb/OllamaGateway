using System.Text.RegularExpressions;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class LocalizationConventionsTests
{
    [TestMethod]
    public void InlineScriptsMustNotContainRazorOrCSharpExpressions()
    {
        var violations = new List<string>();

        foreach (var viewPath in GetViewPaths())
        {
            var source = File.ReadAllText(viewPath);
            foreach (Match scriptMatch in Regex.Matches(
                         source,
                         @"<script(?![^>]*\bsrc\s*=)[^>]*>(?<body>[\s\S]*?)</script>",
                         RegexOptions.IgnoreCase))
            {
                var script = scriptMatch.Groups["body"].Value;
                if (Regex.IsMatch(
                        script,
                        @"@(?:Localizer|Model|Url|Html|Json|ViewData|inject|using|foreach|for|if|switch|\{\s*|\()"))
                {
                    violations.Add(GetRelativePath(viewPath));
                    break;
                }
            }
        }

        Assert.IsEmpty(
            violations,
            "Razor/C# expressions must not be mixed into JavaScript. Pass server values through hidden HTML/data attributes instead: " +
            string.Join(", ", violations));
    }

    [TestMethod]
    public void InlineEventHandlersMustNotContainRazorExpressions()
    {
        var violations = GetViewPaths()
            .Where(viewPath => Regex.IsMatch(
                File.ReadAllText(viewPath),
                @"\bon(?:click|change|submit|input|load|error)\s*=\s*[""'][^""']*@(?!@)",
                RegexOptions.IgnoreCase))
            .Select(GetRelativePath)
            .ToList();

        Assert.IsEmpty(
            violations,
            "Inline event handlers must not mix JavaScript with Razor expressions: " + string.Join(", ", violations));
    }

    [TestMethod]
    public void UserNotificationsInJavaScriptMustComeFromLocalizedDomData()
    {
        var violations = new List<string>();
        var directUserText = new Regex(
            @"\b(?:alert|confirm)\s*\(\s*[""'`]\s*[A-Za-z]|\.(?:innerText|textContent)\s*=\s*[""'`]\s*[A-Za-z]|\.innerHTML\s*=\s*(?:'[^']*?>\s*[A-Za-z][^<]*<|""[^""]*?>\s*[A-Za-z][^<]*<|`[^`]*?>\s*[A-Za-z][^<]*<)",
            RegexOptions.IgnoreCase);

        foreach (var viewPath in GetViewPaths())
        {
            var source = File.ReadAllText(viewPath);
            foreach (Match scriptMatch in Regex.Matches(
                         source,
                         @"<script(?![^>]*\bsrc\s*=)[^>]*>(?<body>[\s\S]*?)</script>",
                         RegexOptions.IgnoreCase))
            {
                if (directUserText.IsMatch(scriptMatch.Groups["body"].Value))
                {
                    violations.Add(GetRelativePath(viewPath));
                    break;
                }
            }
        }

        var scriptsRoot = Path.Combine(GetRepositoryRoot(), "src", "Aiursoft.OllamaGateway", "wwwroot");
        violations.AddRange(Directory
            .EnumerateFiles(scriptsRoot, "*.js", SearchOption.AllDirectories)
            .Where(scriptPath => directUserText.IsMatch(File.ReadAllText(scriptPath)))
            .Select(GetRelativePath));

        Assert.IsEmpty(
            violations,
            "User-facing JavaScript text must be rendered by the server into hidden localized DOM data first: " +
            string.Join(", ", violations));
    }

    private static IEnumerable<string> GetViewPaths()
    {
        var viewsRoot = Path.Combine(GetRepositoryRoot(), "src", "Aiursoft.OllamaGateway", "Views");
        return Directory.EnumerateFiles(viewsRoot, "*.cshtml", SearchOption.AllDirectories);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.EnumerateFiles("*.sln").Any())
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static string GetRelativePath(string path) => Path.GetRelativePath(GetRepositoryRoot(), path);
}
