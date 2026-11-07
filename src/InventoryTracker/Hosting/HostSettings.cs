using Microsoft.Data.Sqlite;

namespace InventoryTracker.Hosting;

/// <summary>
/// Resolved host-level settings: where to listen and where the database lives.
/// <para>
/// Precedence (highest first): command line (<c>--bind</c>, <c>--port</c>, <c>--urls</c>, <c>--db</c>) &gt;
/// environment variables / <c>.env</c> (<c>INVENTORY_BIND</c>, <c>INVENTORY_PORT</c>, <c>INVENTORY_DB</c>,
/// <c>ASPNETCORE_URLS</c>, <c>ConnectionStrings__Default</c>) &gt; <c>appsettings.json</c> &gt; built-in defaults.
/// </para>
/// </summary>
public sealed record HostSettings(string Urls, string ConnectionString, string DatabasePath, string LogDirectory, bool ExposedToNetwork)
{
    public const string BindKey = "INVENTORY_BIND";
    public const string PortKey = "INVENTORY_PORT";
    public const string DbKey = "INVENTORY_DB";
    public const string UrlsKey = "urls";
    public const string ConnectionStringName = "Default";

    public const int DefaultPort = 5080;
    public const string DefaultBind = "localhost";

    public static HostSettings Resolve(IConfiguration config, string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;

        var (urls, exposed) = ResolveUrls(config);
        var (connectionString, dbPath) = ResolveDatabase(config, baseDirectory);
        var logDir = config["Logging:File:Directory"] is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Path.GetDirectoryName(dbPath) ?? baseDirectory, "logs");

        return new HostSettings(urls, connectionString, dbPath, logDir, exposed);
    }

    private static (string Urls, bool Exposed) ResolveUrls(IConfiguration config)
    {
        // An explicit URL list always wins (--urls / ASPNETCORE_URLS).
        if (config[UrlsKey] is { Length: > 0 } explicitUrls)
        {
            return (explicitUrls, IsExposed(explicitUrls));
        }

        var port = DefaultPort;
        if (config[PortKey] is { Length: > 0 } portText)
        {
            if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            {
                throw new InvalidOperationException($"{PortKey} must be a number between 1 and 65535 (got '{portText}').");
            }
        }

        var bind = (config[BindKey] ?? DefaultBind).Trim();
        var host = bind.ToLowerInvariant() switch
        {
            "" or "localhost" or "local" or "loopback" => "localhost",
            "all" or "any" or "lan" or "*" or "0.0.0.0" => "0.0.0.0",
            _ => bind,
        };

        var url = $"http://{host}:{port}";
        return (url, IsExposed(url));
    }

    private static (string ConnectionString, string DatabasePath) ResolveDatabase(IConfiguration config, string baseDirectory)
    {
        string connectionString;
        if (config[DbKey] is { Length: > 0 } path)
        {
            connectionString = $"Data Source={Path.GetFullPath(path)}";
        }
        else if (config.GetConnectionString(ConnectionStringName) is { Length: > 0 } configured)
        {
            connectionString = configured;
        }
        else
        {
            connectionString = $"Data Source={Path.Combine(baseDirectory, "inventory.db")}";
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        return (connectionString, Path.GetFullPath(dataSource));
    }

    private static bool IsExposed(string urls)
        => urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(u =>
            {
                var host = Uri.TryCreate(u.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var uri) ? uri.Host : u;
                return host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1");
            });
}
