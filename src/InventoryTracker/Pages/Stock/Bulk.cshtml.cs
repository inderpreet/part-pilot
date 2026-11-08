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
/// Several movements of one type in one go (a delivery, a job's materials): all lines are posted together or not at all.
/// Lines are added and removed with htmx round-trips of the whole form, so the line indexes stay contiguous and the
/// line-level errors from the service line up with the inputs.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class BulkModel(IStockService stock, IProductService products, ICurrentUser currentUser) : PageModel
{
    public const int MaxLines = 200;

    [BindProperty]
    public BulkForm Input { get; set; } = new();

    private Dictionary<int, string> _labels = [];

    /// <summary>"SKU — Name" for a chosen product, to pre-fill its picker after a round-trip.</summary>
    public string? LabelFor(int? productId) => productId is { } id && _labels.TryGetValue(id, out var label) ? label : null;

    public IActionResult OnGet(StockTransactionType? type)
    {
        Input.Type = type is { } t && MoveModel.Types.Contains(t) ? t : StockTransactionType.Receive;
        Input.Lines = [new BulkLine()];
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Input.Lines ??= [];
        await LoadLabelsAsync();

        if (!MoveModel.Types.Contains(Input.Type))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Type)}", "Choose a movement type.");
        }

        // A half-filled line is a mistake; a completely empty one is just spare room and is dropped.
        for (var i = 0; i < Input.Lines.Count; i++)
        {
            var line = Input.Lines[i];
            if (line.ProductId is null && line.Quantity is not null)
            {
                ModelState.AddModelError($"{nameof(Input)}.Lines[{i}].ProductId", $"Line {i + 1}: choose a product.");
            }
            else if (line.ProductId is not null && line.Quantity is null)
            {
                ModelState.AddModelError($"{nameof(Input)}.Lines[{i}].Quantity", $"Line {i + 1}: enter a quantity.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        Input.Lines.RemoveAll(l => l.ProductId is null && l.Quantity is null);

        var occurredAt = Input.OccurredAt is { } when ? LocalTimeInput.ToUtc(when, Input.Tz) : (DateTime?)null;
        var movements = Input.Lines
            .Select(l => new StockMovement(l.ProductId!.Value, Input.Type, l.Quantity!.Value, Input.Reason, Input.Reference, Input.Note, occurredAt))
            .ToList();

        var result = await stock.PostAsync(movements, currentUser.ToStockContext(), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ModelState.AddStockErrors(result, nameof(Input), keepLineIndex: true, lineLabel: i => $"Line {i + 1}: ");
            EnsureOneLine();
            return Page();
        }

        var noun = movements.Count == 1 ? "line" : "lines";
        this.Flash($"Recorded {movements.Count} {Input.Type.ToString().ToLowerInvariant()} {noun} in one batch.");
        return RedirectToPage("Movements");
    }

    public async Task<IActionResult> OnPostAddLineAsync()
    {
        Input.Lines ??= [];
        if (Input.Lines.Count < MaxLines)
        {
            Input.Lines.Add(new BulkLine());
        }

        await LoadLabelsAsync();
        return Partial("_BulkLines", this);
    }

    public async Task<IActionResult> OnPostRemoveLineAsync(int at)
    {
        Input.Lines ??= [];
        if (at >= 0 && at < Input.Lines.Count)
        {
            Input.Lines.RemoveAt(at);
        }

        EnsureOneLine();
        await LoadLabelsAsync();
        return Partial("_BulkLines", this);
    }

    private void EnsureOneLine()
    {
        if (Input.Lines.Count == 0)
        {
            Input.Lines.Add(new BulkLine());
        }
    }

    private async Task LoadLabelsAsync()
    {
        _labels = [];
        foreach (var id in Input.Lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct())
        {
            if (await products.GetAsync(id, HttpContext.RequestAborted) is { } product)
            {
                _labels[id] = $"{product.Sku} — {product.Name}";
            }
        }
    }

    public sealed class BulkForm
    {
        public StockTransactionType Type { get; set; } = StockTransactionType.Receive;

        [StringLength(StockTransaction.ReasonMaxLength, ErrorMessage = "Reason can be at most 100 characters.")]
        public string? Reason { get; set; }

        [StringLength(StockTransaction.ReferenceMaxLength, ErrorMessage = "Reference can be at most 100 characters.")]
        public string? Reference { get; set; }

        [StringLength(StockTransaction.NoteMaxLength, ErrorMessage = "Note can be at most 500 characters.")]
        public string? Note { get; set; }

        [Display(Name = "Date and time")]
        public DateTime? OccurredAt { get; set; }

        public int? Tz { get; set; }

        public List<BulkLine> Lines { get; set; } = [];
    }

    public sealed class BulkLine
    {
        public int? ProductId { get; set; }

        public int? Quantity { get; set; }
    }
}
