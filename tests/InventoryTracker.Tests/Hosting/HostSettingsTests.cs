using InventoryTracker.Hosting;
using Microsoft.Extensions.Configuration;

namespace InventoryTracker.Tests.Hosting;

public class HostSettingsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "inv-base");

    private static HostSettings Resolve(params (string Key, string Value)[] values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();
        return HostSettings.Resolve(config, Base);
    }

    [Fact]
    public void Defaults_to_localhost_5080_and_exe_dir_database()
    {
        var s = Resolve();

        Assert.Equal("http://localhost:5080", s.Urls);
        Assert.False(s.ExposedToNetwork);
        Assert.Equal(Path.Combine(Base, "inventory.db"), s.DatabasePath);
        Assert.Equal(Path.Combine(Base, "logs"), s.LogDirectory);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("lan")]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    public void Bind_all_listens_on_every_interface(string bind)
    {
        var s = Resolve((HostSettings.BindKey, bind));

        Assert.Equal("http://0.0.0.0:5080", s.Urls);
        Assert.True(s.ExposedToNetwork);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    public void Bind_localhost_is_loopback_only(string bind)
    {
        var s = Resolve((HostSettings.BindKey, bind));

        Assert.Equal("http://localhost:5080", s.Urls);
        Assert.False(s.ExposedToNetwork);
    }

    [Fact]
    public void Bind_accepts_a_specific_address()
    {
        var s = Resolve((HostSettings.BindKey, "192.168.1.20"), (HostSettings.PortKey, "6001"));

        Assert.Equal("http://192.168.1.20:6001", s.Urls);
        Assert.True(s.ExposedToNetwork);
    }

    [Fact]
    public void Explicit_urls_override_bind_and_port()
    {
        var s = Resolve((HostSettings.BindKey, "all"), (HostSettings.PortKey, "7000"), (HostSettings.UrlsKey, "http://localhost:6000"));

        Assert.Equal("http://localhost:6000", s.Urls);
        Assert.False(s.ExposedToNetwork);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void Invalid_port_is_rejected(string port)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve((HostSettings.PortKey, port)));
        Assert.Contains(HostSettings.PortKey, ex.Message);
    }

    [Fact]
    public void Db_option_sets_connection_string_and_log_directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "somewhere", "x.db");

        var s = Resolve((HostSettings.DbKey, path));

        Assert.Equal(path, s.DatabasePath);
        Assert.Equal($"Data Source={path}", s.ConnectionString);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(path)!, "logs"), s.LogDirectory);
    }

    [Fact]
    public void Connection_string_setting_is_used_when_no_db_option()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs", "y.db");

        var s = Resolve(("ConnectionStrings:Default", $"Data Source={path}"));

        Assert.Equal(path, s.DatabasePath);
    }

    [Fact]
    public void Db_option_beats_connection_string()
    {
        var s = Resolve((HostSettings.DbKey, "a.db"), ("ConnectionStrings:Default", "Data Source=b.db"));

        Assert.Equal(Path.GetFullPath("a.db"), s.DatabasePath);
    }

    [Fact]
    public void Cli_flags_map_onto_the_same_keys_and_open_is_stripped()
    {
        var cli = CliOptions.Parse(["--open", "--bind", "all", "--port=6100", "--db", "C:\\temp\\x.db", "--urls", "http://localhost:6000"]);

        Assert.True(cli.OpenBrowser);
        Assert.DoesNotContain("--open", cli.ConfigurationArgs);

        var config = new ConfigurationBuilder().AddCommandLine(cli.ConfigurationArgs, CliOptions.SwitchMappings).Build();
        Assert.Equal("all", config[HostSettings.BindKey]);
        Assert.Equal("6100", config[HostSettings.PortKey]);
        Assert.Equal("C:\\temp\\x.db", config[HostSettings.DbKey]);

        var s = HostSettings.Resolve(config, Base);
        Assert.Equal("http://localhost:6000", s.Urls);
        Assert.Equal("C:\\temp\\x.db", s.DatabasePath);
    }

    [Fact]
    public void Open_flag_does_not_swallow_the_next_option()
    {
        var cli = CliOptions.Parse(["--open", "--db", "x.db"]);

        var config = new ConfigurationBuilder().AddCommandLine(cli.ConfigurationArgs, CliOptions.SwitchMappings).Build();
        Assert.Equal("x.db", config[HostSettings.DbKey]);
    }
}
