using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests;

/// <summary>Helpers for driving server-rendered forms: read the antiforgery token, post fields, sign in through the real form.</summary>
public static partial class WebForms
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    public static string ExtractToken(string html)
    {
        var match = TokenPattern().Match(html);
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("No antiforgery token on the page.");
    }

    public static async Task<string> TokenAsync(HttpClient client, string url) =>
        ExtractToken(await client.GetStringAsync(url));

    /// <summary>GETs <paramref name="tokenPage"/> (default: the target itself) for a token, then POSTs the fields to <paramref name="url"/>.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, IEnumerable<(string Key, string Value)> fields, string? tokenPage = null)
    {
        var token = await TokenAsync(client, tokenPage ?? url);
        var content = new FormUrlEncodedContent(fields
            .Prepend((Key: "__RequestVerificationToken", Value: token))
            .Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await client.PostAsync(url, content);
    }

    /// <summary>Signs in through the real login form (cookie auth). Returns the response so callers can assert on failures.</summary>
    public static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string userName, string password, bool rememberMe = false, string? returnUrl = null)
    {
        var fields = new List<(string, string)> { ("Input.UserName", userName), ("Input.Password", password), ("Input.RememberMe", rememberMe ? "true" : "false") };
        if (returnUrl is not null)
        {
            fields.Add(("ReturnUrl", returnUrl));
        }

        var page = returnUrl is null ? "/Account/Login" : "/Account/Login?ReturnUrl=" + Uri.EscapeDataString(returnUrl);
        return PostFormAsync(client, "/Account/Login", fields, page);
    }

    public static async Task<string> UserIdAsync(AppFactory factory, string userName)
    {
        var id = "";
        await factory.WithScopeAsync(async sp =>
        {
            var user = await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByNameAsync(userName);
            id = user!.Id;
        });
        return id;
    }

    public static async Task SetActiveAsync(AppFactory factory, string userName, bool active)
    {
        await factory.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<IUserService>();
            var dto = (await users.GetAsync(await UserIdAsync(factory, userName)))!;
            var result = await users.UpdateAsync(new UpdateUserInput(dto.Id, dto.DisplayName, dto.Email, dto.Role!, active));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        });
    }
}
