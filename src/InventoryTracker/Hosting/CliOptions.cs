namespace InventoryTracker.Hosting;

/// <summary>
/// Handles the app's own command-line flags. <c>--open</c> is a bare flag, which the .NET command-line configuration
/// provider cannot represent safely, so it is removed here and everything else is passed through as configuration.
/// </summary>
public sealed record CliOptions(string[] ConfigurationArgs, bool OpenBrowser)
{
    /// <summary>Command-line switch mappings: <c>--db</c>, <c>--bind</c>, <c>--port</c> (and standard <c>--urls</c>).</summary>
    public static IDictionary<string, string> SwitchMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["--db"] = HostSettings.DbKey,
        ["--bind"] = HostSettings.BindKey,
        ["--port"] = HostSettings.PortKey,
    };

    public static CliOptions Parse(string[] args)
    {
        var open = false;
        var rest = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase))
            {
                open = true;
            }
            else
            {
                rest.Add(arg);
            }
        }

        return new CliOptions(rest.ToArray(), open);
    }
}
