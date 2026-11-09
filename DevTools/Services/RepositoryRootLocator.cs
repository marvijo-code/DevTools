namespace DevTools.Services;

/// <summary>
/// Finds the folder that holds the git repositories on this machine. Two layouts are common: a Visual
/// Studio style <c>%USERPROFILE%\source\repos</c>, and the Windows dev box's <c>C:\dev</c>. The folder
/// usually exists but can be empty (for example when one drive moved), so a candidate only wins when it
/// actually contains a checkout.
/// </summary>
internal static class RepositoryRootLocator
{
    public static string Resolve()
    {
        var configuredPath = Environment.GetEnvironmentVariable("REPOS_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var sourceRepos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        const string DevRoot = @"C:\dev";

        foreach (var candidate in new[] { sourceRepos, DevRoot })
        {
            if (HasRepositories(candidate))
            {
                return candidate;
            }
        }

        return Directory.Exists(DevRoot) || !Directory.Exists(sourceRepos) ? DevRoot : sourceRepos;
    }

    /// <summary>True when the folder holds at least one normal checkout (a <c>.git</c> folder).</summary>
    public static bool HasRepositories(string rootPath)
    {
        try
        {
            return Directory.Exists(rootPath) &&
                   Directory.EnumerateDirectories(rootPath).Any(path => Directory.Exists(Path.Combine(path, ".git")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}