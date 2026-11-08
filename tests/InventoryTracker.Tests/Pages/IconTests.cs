using System.Net;
using System.Text.RegularExpressions;

namespace InventoryTracker.Tests.Pages;

/// <summary>The logo (branding/icon.jpg) reaches the browser as favicon, touch icon and sidebar / sign-in mark.</summary>
public sealed partial class IconTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    [GeneratedRegex("<link rel=\"icon\" href=\"/favicon\\.ico[^\"]*\" sizes=\"any\"")]
    private static partial Regex FaviconLink();

    [Fact]
    public async Task Favicon_is_served_to_anonymous_visitors_as_a_valid_multi_size_ico()
    {
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync("/favicon.ico");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Content.Headers.ContentType?.MediaType, new[] { "image/x-icon", "image/vnd.microsoft.icon" });
        Assert.Equal([0, 0, 1, 0], bytes[..4]); // ICO header: reserved 0, type 1
        Assert.True(BitConverter.ToUInt16(bytes, 4) >= 4, "16, 32, 48 and 256 px images are expected.");
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/img/apple-touch-icon.png", 180, 180)]
    [InlineData("/img/icon-192.png", 192, 192)]
    public async Task Touch_icons_are_square_pngs(string path, int width, int height)
    {
        using var client = factory.CreateAnonymousClient();

        var bytes = await client.GetByteArrayAsync(path);

        Assert.Equal(PngSignature, bytes[..8]);
        Assert.Equal(width, ReadBigEndianInt(bytes, 16));
        Assert.Equal(height, ReadBigEndianInt(bytes, 20));
    }

    [Fact]
    public async Task Logo_is_a_png_with_an_alpha_channel()
    {
        using var client = factory.CreateAnonymousClient();

        var bytes = await client.GetByteArrayAsync("/img/logo.png");

        Assert.Equal(PngSignature, bytes[..8]);
        Assert.Equal(6, bytes[25]); // IHDR colour type 6 = RGBA
    }

    [Fact]
    public async Task Signed_in_pages_link_the_icons_and_show_the_logo_in_the_rail()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Matches(FaviconLink(), html);
        Assert.Contains("rel=\"apple-touch-icon\" href=\"/img/apple-touch-icon.png\"", html);
        Assert.Matches("<a class=\"rail-brand\" href=\"/\">\\s*<img class=\"mark\" src=\"/img/logo.png\" alt=\"\"", html);
    }

    [Fact]
    public async Task The_sign_in_page_shows_the_logo_above_the_name()
    {
        using var client = factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/Account/Login");

        Assert.Matches(FaviconLink(), html);
        Assert.Matches("<a class=\"brand\" href=\"/\"><img class=\"brand-logo\" src=\"/img/logo.png\" alt=\"\"[^>]*/><strong>", html);
    }

    private static int ReadBigEndianInt(byte[] bytes, int offset)
        => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
