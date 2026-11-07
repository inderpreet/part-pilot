namespace InventoryTracker.Tests;

public static class HttpAssertions
{
    /// <summary>
    /// The redirect target as path + query. The cookie handler emits an absolute URL for challenges while page
    /// redirects are relative, so tests compare on the part the app controls.
    /// </summary>
    public static string RedirectPath(this HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException(
            $"Response {(int)response.StatusCode} has no Location header.");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }
}
