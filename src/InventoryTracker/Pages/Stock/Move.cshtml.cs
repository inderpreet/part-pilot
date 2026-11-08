using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Stock;

/// <summary>
/// Records one stock movement. Opened as a dialog (htmx loads it into <c>#modal</c>) from the product list and detail
/// page; the same URL is also a plain full page, so it works without JavaScript and as a link.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class MoveModel(IStockService stock, IProductService products, ICurrentUser currentUser) : PageModel
{
    /// <summary>The types the form offers. Counts have their own page; reversals are made from the history list.</summary>
    public static readonly IReadOnlyList<StockTransactionType> Types =
        [StockTransactionType.Receive, StockTransactionType.Issue, StockTransactionType.Adjust, StockTransactionType.Return];

    [BindProperty]
    public MoveForm Input { get; set; } = new();

    public ProductDetail? Product { get; private set; }

    /// <summary>True when rendered into the dialog (any htmx request); false for the full page.</summary>
    public bool IsModal => this.IsHtmx();

    public async Task<IActionResult> OnGetAsync(int? productId, StockTransactionType? type)
    {
        Input.ProductId = productId;
        Input.Type = type is { } t && Types.Contains(t) ? t : StockTransactionType.Receive;

        await LoadProductAsync();
        if (productId is not null && Product is null)
        {
            return NotFound();
        }

        return IsModal ? Partial("_MoveModal", this) : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadProductAsync();

        if (Input.ProductId is null)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ProductId)}", "Choose a product.");
        }

        if (!Types.Contains(Input.Type))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Type)}", "Choose a movement type.");
        }

        if (!ModelState.IsValid || Input.ProductId is null || Input.Quantity is null)
        {
            return Invalid();
        }

        var line = new StockMovement(
            Input.ProductId.Value,
            Input.Type,
            Input.Quantity.Value,
            Input.Reason,
            Input.Reference,
            Input.Note,
            Input.OccurredAt is { } when ? LocalTimeInput.ToUtc(when, Input.Tz) : null);

        var result = await stock.PostAsync([line], currentUser.ToStockContext(), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ModelState.AddStockErrors(result, nameof(Input));
            return Invalid();
        }

        var posted = result.Value[0];
        TempData[PageExtensions.FlashKey] = Summary(posted);

        // The flash waits in TempData, so a dialog reloads the page behind it; the full page goes to the product.
        if (IsModal)
        {
            Response.Headers["HX-Refresh"] = "true";
            return new NoContentResult();
        }

        return RedirectToPage("/Products/Detail", new { id = posted.ProductId });
    }

    private string Summary(PostedMovement posted)
    {
        var sku = Product?.Sku ?? "product";
        var unit = Product?.Unit ?? "units";
        var quantity = Math.Abs(posted.QuantityDelta);
        var message = Input.Type switch
        {
            StockTransactionType.Receive => $"Received {quantity} {unit} of {sku}.",
            StockTransactionType.Issue => $"Issued {quantity} {unit} of {sku}.",
            StockTransactionType.Return => $"Returned {quantity} {unit} of {sku} to stock.",
            _ => $"Adjusted {sku} by {(posted.QuantityDelta > 0 ? "+" : "-")}{quantity}.",
        };
        return $"{message} On hand: {posted.QuantityAfter}.";
    }

    private IActionResult Invalid()
    {
        if (!IsModal)
        {
            return Page();
        }

        // 422 is swapped into the dialog by site.js; the form partial replaces itself and shows the errors.
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Partial("_MoveForm", this);
    }

    private async Task LoadProductAsync()
    {
        if (Input.ProductId is { } id)
        {
            Product = await products.GetAsync(id, HttpContext.RequestAborted);
        }
    }

    public sealed class MoveForm
    {
        public int? ProductId { get; set; }

        public StockTransactionType Type { get; set; } = StockTransactionType.Receive;

        [Required(ErrorMessage = "Enter a quantity.")]
        public int? Quantity { get; set; }

        [StringLength(StockTransaction.ReasonMaxLength, ErrorMessage = "Reason can be at most 100 characters.")]
        public string? Reason { get; set; }

        [StringLength(StockTransaction.ReferenceMaxLength, ErrorMessage = "Reference can be at most 100 characters.")]
        public string? Reference { get; set; }

        [StringLength(StockTransaction.NoteMaxLength, ErrorMessage = "Note can be at most 500 characters.")]
        public string? Note { get; set; }

        /// <summary>When it happened, as typed into a <c>datetime-local</c> input (blank = now).</summary>
        [Display(Name = "Date and time")]
        public DateTime? OccurredAt { get; set; }

        /// <summary>The browser's time-zone offset in minutes (<see cref="LocalTimeInput"/>); filled by site.js.</summary>
        public int? Tz { get; set; }
    }
}
