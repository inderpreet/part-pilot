using System.Diagnostics;
using InventoryTracker.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public int HttpStatus { get; private set; } = StatusCodes.Status500InternalServerError;

    public string Title { get; private set; } = "Something went wrong";

    public string Message { get; private set; } = "An unexpected error occurred. Please try again.";

    public string? RequestId { get; private set; }

    public IActionResult OnGet(int? statusCode) => Render(statusCode);

    public IActionResult OnPost(int? statusCode) => Render(statusCode);

    private IActionResult Render(int? statusCode)
    {
        HttpStatus = statusCode ?? (Response.StatusCode >= 400 ? Response.StatusCode : StatusCodes.Status500InternalServerError);
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        (Title, Message) = HttpStatus switch
        {
            StatusCodes.Status400BadRequest => ("Request rejected", "The request could not be processed. Reload the page and try again."),
            StatusCodes.Status403Forbidden => ("Access denied", "You don't have permission to do that."),
            StatusCodes.Status404NotFound => ("Not found", "The page or item you asked for doesn't exist."),
            _ => ("Something went wrong", "An unexpected error occurred. Please try again."),
        };

        Response.StatusCode = HttpStatus;

        if (this.IsHtmx())
        {
            // Show the error in the flash area instead of replacing the page content.
            Response.Headers["HX-Retarget"] = "#flash";
            Response.Headers["HX-Reswap"] = "innerHTML";
            return Partial("_ErrorPartial", this);
        }

        return Page();
    }
}
