using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Canonical path-prefix policy for the single filename catalog. It deliberately
/// avoids broad folder-name-only rules: a user directory named "Windows" or "bin"
/// remains searchable when it is outside a known system/cache root. Only exact,
/// conventional dependency/cache directory names are filtered at any depth.
/// </summary>
internal static class SearchIndexPathPolicy
{
    private static readonly string[] s_rootRelativeSystemTrees =
    [
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "Config.Msi",
        "PerfLogs",
        "MSOCache"
    ];

    private static readonly string[] s_cacheDirectoryComponents =
    [
        ".cache",
        ".git",
        ".gradle",
        ".hg",
        ".mypy_cache",
        ".next",
        ".nuget",
        ".nuxt",
        ".pnpm-store",
        ".pytest_cache",
        ".ruff_cache",
        ".svn",
        ".tox",
        ".venv",
        "__pycache__",
        "node_modules",
        "site-packages",
        "venv"
    ];

    private static readonly Lazy<string[]> s_defaultExcludedRoots = new(
        BuildDefaultExcludedRoots,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IReadOnlyList<string> DefaultExcludedRoots => s_defaultExcludedRoots.Value;

    internal static bool ShouldExcludeFromIndex(string? path, AppSettings settings)
    {
        if (!TryNormalize(path, out string normalized))
        {
            return false;
        }

        if (settings.SearchIndexExcludedPaths is { Count: > 0 })
        {
            foreach (string configured in settings.SearchIndexExcludedPaths)
            {
                if (TryNormalize(Environment.ExpandEnvironmentVariables(configured), out string excluded) &&
                    IsSameOrDescendant(normalized, excluded))
                {
                    return true;
                }
            }
        }

        if (!settings.SearchHideSystemNoise)
        {
            return false;
        }

        foreach (string excluded in s_defaultExcludedRoots.Value)
        {
            if (IsSameOrDescendant(normalized, excluded))
            {
                return true;
            }
        }

        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root) || normalized.Length <= root.Length)
        {
            return false;
        }

        ReadOnlySpan<char> relative = normalized.AsSpan(root.Length);
        while (!relative.IsEmpty && relative[0] is '\\' or '/')
        {
            relative = relative[1..];
        }
        foreach (string tree in s_rootRelativeSystemTrees)
        {
            if (StartsWithPathComponent(relative, tree))
            {
                return true;
            }
        }

        if (ContainsCacheDirectoryComponent(relative))
        {
            return true;
        }

        return false;
    }

    internal static bool IsSameOrDescendant(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.Length > root.Length &&
               candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               (root.EndsWith(Path.DirectorySeparatorChar) ||
                root.EndsWith(Path.AltDirectorySeparatorChar) ||
                candidate[root.Length] is '\\' or '/');
    }

    private static string[] BuildDefaultExcludedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (TryNormalize(path, out string normalized))
            {
                roots.Add(normalized);
            }
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            // Start-menu shortcuts and installed application files remain covered
            // elsewhere. Indexing the operating-system tree adds hundreds of
            // thousands of low-value names and can crowd out user files.
            Add(windows);
        }

        string commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonApplicationData))
        {
            Add(commonApplicationData);
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        Add(Path.GetTempPath());
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return normalized.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartsWithPathComponent(ReadOnlySpan<char> path, string component)
    {
        if (!path.StartsWith(component, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == component.Length || path[component.Length] is '\\' or '/';
    }

    private static bool ContainsCacheDirectoryComponent(ReadOnlySpan<char> path)
    {
        while (!path.IsEmpty)
        {
            while (!path.IsEmpty && path[0] is '\\' or '/')
            {
                path = path[1..];
            }

            int separator = path.IndexOfAny('\\', '/');
            ReadOnlySpan<char> component = separator >= 0 ? path[..separator] : path;
            foreach (string excluded in s_cacheDirectoryComponents)
            {
                if (component.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (separator < 0)
            {
                break;
            }

            path = path[(separator + 1)..];
        }

        return false;
    }
}
