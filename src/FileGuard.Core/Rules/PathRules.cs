namespace FileGuard.Core.Rules;

/// <summary>
/// 路径过滤规则：默认忽略的目录/文件、只对文本类扩展名生效、豁免目录判断。
/// </summary>
public sealed class PathRules
{
    public static PathRules Default { get; } = new();

    private static readonly HashSet<string> IgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "bower_components",
        "bin", "obj", "dist", "build", "out", "target",
        ".vs", ".vscode", ".idea", ".next", ".nuxt", ".cache", ".turbo",
        "packages", "vendor", ".venv", "venv", "env", "__pycache__", ".pytest_cache",
        "coverage", ".gradle", ".m2", "Pods",
    };

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "composer.lock",
        "Cargo.lock", "poetry.lock", "Gemfile.lock", "Pipfile.lock", "go.sum",
    };

    private static readonly string[] IgnoredSuffixes =
    {
        ".lock", ".tmp", ".temp", ".swp", ".swx", ".part", ".partial", ".crdownload", ".bak~",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".vb",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".mts", ".cts", ".vue", ".svelte", ".astro",
        ".py", ".pyi", ".rb", ".php", ".java", ".kt", ".kts", ".scala", ".groovy", ".gradle",
        ".go", ".rs", ".swift", ".m", ".mm", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh",
        ".lua", ".r", ".dart", ".ex", ".exs", ".clj", ".elm", ".zig", ".nim",
        ".sh", ".bash", ".zsh", ".fish", ".bat", ".cmd", ".ps1", ".psm1", ".psd1",
        ".sql", ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".styl",
        ".md", ".markdown", ".rst", ".adoc", ".txt", ".tex",
        ".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".properties", ".env", ".xml", ".xsd", ".plist", ".csv", ".tsv",
        ".graphql", ".gql", ".proto", ".tf", ".hcl",
    };

    /// <summary>是否属于默认忽略的目录、锁文件或临时文件。</summary>
    public bool IsIgnoredPath(string fullPath, string rootPath)
    {
        string rel;
        try
        {
            rel = Path.GetRelativePath(rootPath, fullPath);
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (rel.StartsWith("..", StringComparison.Ordinal)) return true;

        var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return true;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (IgnoredDirNames.Contains(segments[i])) return true;
        }

        var fileName = segments[^1];
        if (IgnoredFileNames.Contains(fileName)) return true;
        if (fileName.StartsWith("~$", StringComparison.Ordinal)) return true;

        foreach (var suffix in IgnoredSuffixes)
        {
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>是否属于我们监视的文本类文件。二进制文件一律不管。</summary>
    public bool IsTextFile(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        return !string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext);
    }

    /// <summary>path 是否位于 directory 之下（含自身）。不区分大小写。</summary>
    public static bool IsUnder(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;

        string p, d;
        try
        {
            p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (p.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
        return p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
