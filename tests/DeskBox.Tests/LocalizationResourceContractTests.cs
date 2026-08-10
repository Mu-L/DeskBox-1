using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed partial class LocalizationResourceContractTests
{
    [Fact]
    public void EveryLocale_HasTheSameKeysAsEnglish()
    {
        string stringsDirectory = Path.Combine(FindRepositoryRoot(), "src", "DeskBox", "Strings");
        HashSet<string> englishKeys = ReadKeys(Path.Combine(stringsDirectory, "en-US.json"));

        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            HashSet<string> localizedKeys = ReadKeys(path);
            Assert.True(
                englishKeys.SetEquals(localizedKeys),
                $"{Path.GetFileName(path)} differs from en-US.json. " +
                $"Missing: {string.Join(", ", englishKeys.Except(localizedKeys).Order())}; " +
                $"Extra: {string.Join(", ", localizedKeys.Except(englishKeys).Order())}.");
        }
    }

    [Fact]
    public void LiteralLocalizationCalls_ReferenceExistingResources()
    {
        string root = FindRepositoryRoot();
        string sourceRoot = Path.Combine(root, "src", "DeskBox");
        HashSet<string> englishKeys = ReadKeys(Path.Combine(sourceRoot, "Strings", "en-US.json"));
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            string source = File.ReadAllText(path);
            foreach (Match match in LiteralLocalizationCall().Matches(source))
            {
                string key = match.Groups[1].Value;
                if (!englishKeys.Contains(key))
                {
                    missing.Add(key);
                }
            }
        }

        Assert.True(missing.Count == 0, $"Missing localization resources: {string.Join(", ", missing)}.");
    }

    private static HashSet<string> ReadKeys(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }

    [GeneratedRegex("\\.(?:T|Format)\\(\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralLocalizationCall();
}
