using System.Text.RegularExpressions;
using InventoryTracker.Auth;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace InventoryTracker.Hosting;

/// <summary>Glue between the stock pages and <see cref="IStockService"/>.</summary>
public static partial class StockPageExtensions
{
    [GeneratedRegex(@"^Lines\[(?<index>\d+)\]\.(?<field>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LineField();

    /// <summary>Who is posting: the signed-in user. Pages are <c>CanEdit</c>-protected, so a missing id is a bug, not a user error.</summary>
    public static StockContext ToStockContext(this ICurrentUser user)
        => new(user.Id ?? throw new InvalidOperationException("Stock can only be posted by a signed-in user."));

    /// <summary>
    /// Maps the errors of a failed stock <see cref="Result"/> onto form fields. The service reports <c>Lines[n].Field</c> paths:
    /// with <paramref name="keepLineIndex"/> (a multi-line form whose inputs are <c>Input.Lines[n].Field</c>) they keep the index,
    /// otherwise (a single-line form with plain <c>Input.Field</c> inputs) the prefix is dropped. <c>Product</c> maps to the
    /// <c>ProductId</c> input. When <paramref name="lineLabel"/> is given it prefixes the message ("Line 3: ..."), so the
    /// summary at the top of a long form says which line is meant. A <c>null</c> <paramref name="prefix"/> reports every error at form level (no field), still with the line label.
    /// </summary>
    public static void AddStockErrors(
        this ModelStateDictionary modelState,
        Result result,
        string? prefix,
        bool keepLineIndex = false,
        Func<int, string>? lineLabel = null)
    {
        foreach (var error in result.Errors)
        {
            var message = error.Message;
            var key = string.Empty;

            if (error.Field is { } field)
            {
                var match = LineField().Match(field);
                var name = match.Success ? match.Groups["field"].Value : field;
                if (name == StockFields.Product)
                {
                    name = "ProductId";
                }

                if (match.Success)
                {
                    var index = int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (lineLabel is not null)
                    {
                        message = $"{lineLabel(index)}{message}";
                    }

                    if (prefix is not null)
                    {
                        key = keepLineIndex ? $"{prefix}.Lines[{index}].{name}" : $"{prefix}.{name}";
                    }
                }
                else if (prefix is not null)
                {
                    key = $"{prefix}.{name}";
                }
            }

            modelState.AddModelError(key, message);
        }
    }
}
