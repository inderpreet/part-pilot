namespace InventoryTracker.Hosting;

/// <summary>
/// Minimal <c>.env</c> loader. Values are exported as process environment variables, but never override a variable
/// that is already set, so precedence is: real environment &gt; <c>.env</c> in the working directory &gt;
/// <c>.env</c> beside the executable.
/// </summary>
public static class DotEnv
{
    public const string FileName = ".env";

    /// <summary>Loads <c>.env</c> from the working directory, then from the executable's directory.</summary>
    public static void LoadDefaults()
        => Load(Path.Combine(Directory.GetCurrentDirectory(), FileName), Path.Combine(AppContext.BaseDirectory, FileName));

    public static void Load(params string[] paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var (key, value) in Parse(File.ReadLines(path)))
            {
                if (Environment.GetEnvironmentVariable(key) is null)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
    }

    /// <summary>Parses <c>KEY=VALUE</c> lines. Supports comments, <c>export</c> prefix and quoted values.</summary>
    public static IEnumerable<(string Key, string Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            else
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                {
                    value = value[..hash].TrimEnd();
                }
            }

            yield return (key, value);
        }
    }
}
