using InventoryTracker.Hosting;

namespace InventoryTracker.Tests.Hosting;

public sealed class DotEnvTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotenv-").FullName;
    private readonly List<string> _keys = [];

    private string Key(string name)
    {
        var key = $"INVTEST_{name}_{Guid.NewGuid():N}";
        _keys.Add(key);
        return key;
    }

    [Fact]
    public void Parse_handles_comments_quotes_export_and_inline_comments()
    {
        var parsed = DotEnv.Parse(
        [
            "# a comment",
            "",
            "A=1",
            "  B = two words  ",
            "export C=3",
            "D=\"quoted # not a comment\"",
            "E='single'",
            "F=value # trailing comment",
            "G=a=b",
            "no-equals-sign",
            "=novalue",
        ]).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("1", parsed["A"]);
        Assert.Equal("two words", parsed["B"]);
        Assert.Equal("3", parsed["C"]);
        Assert.Equal("quoted # not a comment", parsed["D"]);
        Assert.Equal("single", parsed["E"]);
        Assert.Equal("value", parsed["F"]);
        Assert.Equal("a=b", parsed["G"]);
        Assert.Equal(7, parsed.Count);
    }

    [Fact]
    public void Load_sets_missing_variables_but_never_overrides_existing_ones()
    {
        var fresh = Key("FRESH");
        var existing = Key("EXISTING");
        Environment.SetEnvironmentVariable(existing, "from-real-env");
        var file = Path.Combine(_dir, ".env");
        File.WriteAllText(file, $"{fresh}=from-file\n{existing}=from-file\n");

        DotEnv.Load(file);

        Assert.Equal("from-file", Environment.GetEnvironmentVariable(fresh));
        Assert.Equal("from-real-env", Environment.GetEnvironmentVariable(existing));
    }

    [Fact]
    public void Earlier_files_take_precedence_and_missing_files_are_ignored()
    {
        var key = Key("MULTI");
        var first = Path.Combine(_dir, "first.env");
        var second = Path.Combine(_dir, "second.env");
        File.WriteAllText(first, $"{key}=first");
        File.WriteAllText(second, $"{key}=second");

        DotEnv.Load(Path.Combine(_dir, "missing.env"), first, second);

        Assert.Equal("first", Environment.GetEnvironmentVariable(key));
    }

    public void Dispose()
    {
        foreach (var key in _keys)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        Directory.Delete(_dir, recursive: true);
    }
}
