using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InventoryTracker.Tests.Pages;

public class HostSmokeTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [Fact]
    public async Task Startup_creates_the_database_file()
    {
        using var client = factory.CreateClient();
        await client.GetAsync("/health");

        Assert.True(File.Exists(factory.DatabasePath));
    }

    [Fact]
    public async Task Health_returns_200_json_with_version_and_db_status()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("ok", json.RootElement.GetProperty("db").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Health_returns_503_when_the_database_check_fails()
    {
        using var failing = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.Configure<HealthCheckServiceOptions>(o =>
            {
                o.Registrations.Clear();
                o.Registrations.Add(new HealthCheckRegistration(
                    "database", _ => new AlwaysUnhealthy(), HealthStatus.Unhealthy, tags: null));
            })));
        using var client = failing.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("error", json.RootElement.GetProperty("db").GetString());
    }

    [Fact]
    public async Task Static_files_are_served_from_embedded_resources_without_a_physical_wwwroot()
    {
        var emptyRoot = Directory.CreateTempSubdirectory("no-wwwroot-").FullName;
        try
        {
            using var isolated = new AppFactory { ContentRootOverride = emptyRoot };
            using var client = isolated.CreateClient();
            Assert.False(Directory.Exists(Path.Combine(emptyRoot, "wwwroot")));

            var response = await client.GetAsync("/js/site.js");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Application script", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Directory.Delete(emptyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_url_renders_the_full_not_found_page()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<html", html);
        Assert.Contains("Not found", html);
    }

    [Fact]
    public async Task Unhandled_exception_renders_the_full_error_page_without_leaking_details()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/__fault");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<html", html);
        Assert.Contains("Something went wrong", html);
        Assert.DoesNotContain("Test fault", html);
    }

    [Fact]
    public async Task Unhandled_exception_on_an_htmx_request_returns_a_partial_retargeted_to_flash()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__fault");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("#flash", response.Headers.GetValues("HX-Retarget").Single());
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", html);
        Assert.Contains("role=\"alert\"", html);
    }

    private sealed class AlwaysUnhealthy : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Unhealthy("down"));
    }
}
