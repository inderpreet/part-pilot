using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>
/// The only way on-hand quantity changes (architecture invariants 1-3). Every change is an immutable
/// <see cref="StockTransaction"/> plus the matching update of <c>Product.QuantityOnHand</c>, in one database transaction.
/// </summary>
public interface IStockService
{
    /// <summary>
    /// Posts all <paramref name="lines"/> or none of them. Every line is validated and every problem reported (errors carry
    /// <see cref="StockFields.Line"/> paths) before anything is written. A <c>Count</c> line posts the difference between the
    /// counted and the current quantity and is skipped (no transaction) when they match.
    /// Joins the caller's transaction when one is open on the context (e.g. PO receiving); otherwise opens its own.
    /// </summary>
    Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(
        IReadOnlyList<StockMovement> lines, StockContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a transaction with an opposite one. Not allowed for a Reversal, for something already reversed, or for a purchase
    /// order receipt (those are reversed through <c>PurchaseOrderService</c>, which also lowers the line's received quantity).
    /// </summary>
    Task<Result<PostedMovement>> ReverseAsync(
        long transactionId, StockContext context, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses a purchase order receipt: the counterpart of <see cref="ReverseAsync"/> for rows that carry a
    /// <c>PurchaseOrderLineId</c>. Only <c>PurchaseOrderService</c> calls this, inside its own transaction.
    /// </summary>
    Task<Result<PostedMovement>> ReverseOrderReceiptAsync(
        long transactionId, StockContext context, string? note = null, CancellationToken cancellationToken = default);

    Task<StockMovementItem?> GetAsync(long transactionId, CancellationToken cancellationToken = default);

    /// <summary>A product's ledger, newest posting first.</summary>
    Task<PagedResult<StockMovementItem>> GetHistoryAsync(int productId, ListQuery paging, CancellationToken cancellationToken = default);

    /// <summary>The global ledger with filters, newest posting first.</summary>
    Task<PagedResult<StockMovementItem>> SearchMovementsAsync(MovementQuery query, CancellationToken cancellationToken = default);

    /// <summary>Everyone who has posted at least one transaction, by display name (for the movements filter).</summary>
    Task<IReadOnlyList<MovementUser>> ListMovementUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows what posting these counts would change. Unknown products are left out. Writes nothing.</summary>
    Task<IReadOnlyList<CountPreviewLine>> PreviewCountAsync(IReadOnlyList<CountEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Recomputes every product's on-hand from the ledger. With <paramref name="fix"/> the cached values are rewritten to match.</summary>
    Task<StockVerifyReport> VerifyAsync(bool fix = false, CancellationToken cancellationToken = default);
}

public sealed class StockService(AppDbContext db, ISettingsService settings, IClock clock, ILogger<StockService> logger) : IStockService
{
    /// <summary>Largest on-hand quantity (and largest single movement). Keeps every sum comfortably inside <see cref="int"/>.</summary>
    public const int MaxQuantity = 1_000_000_000;

    /// <summary>How far ahead of the server clock an <c>OccurredAt</c> may be (a LAN device's clock can run fast). Clamped to now.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public async Task<Result<IReadOnlyList<PostedMovement>>> PostAsync(
        IReadOnlyList<StockMovement> lines, StockContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        RequireUser(context);

        if (lines.Count == 0)
        {
            return Result.Failure<IReadOnlyList<PostedMovement>>("stock.no_lines", "Add at least one line.", StockFields.Lines);
        }

        var now = clock.UtcNow;
        var errors = new List<Error>();
        var planned = new List<Planned>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            planned.Add(Validate(i, lines[i], now, errors));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<IReadOnlyList<PostedMovement>>([.. errors]);
        }

        // Read before the write transaction opens: the settings service uses its own connection.
        var allowNegative = await settings.GetBoolAsync(SettingKeys.StockAllowNegative, cancellationToken);

        return await InTransactionAsync(
            () => PostCoreAsync(planned, context, allowNegative, cancellationToken),
            result => result.Succeeded,
            cancellationToken);
    }

    public Task<Result<PostedMovement>> ReverseAsync(
        long transactionId, StockContext context, string? note = null, CancellationToken cancellationToken = default)
        => ReverseCoreInTransactionAsync(transactionId, context, note, orderReceipt: false, cancellationToken);

    public Task<Result<PostedMovement>> ReverseOrderReceiptAsync(
        long transactionId, StockContext context, string? note = null, CancellationToken cancellationToken = default)
        => ReverseCoreInTransactionAsync(transactionId, context, note, orderReceipt: true, cancellationToken);

    private async Task<Result<PostedMovement>> ReverseCoreInTransactionAsync(
        long transactionId, StockContext context, string? note, bool orderReceipt, CancellationToken cancellationToken)
    {
        RequireUser(context);

        var cleanNote = Clean(note);
        if (cleanNote is { Length: > StockTransaction.NoteMaxLength })
        {
            return Result.Failure<PostedMovement>("stock.note_too_long", $"Note can be at most {StockTransaction.NoteMaxLength} characters.", StockFields.Note);
        }

        var allowNegative = await settings.GetBoolAsync(SettingKeys.StockAllowNegative, cancellationToken);
        var now = clock.UtcNow;

        return await InTransactionAsync(
            () => ReverseCoreAsync(transactionId, context, cleanNote, allowNegative, now, orderReceipt, cancellationToken),
            result => result.Succeeded,
            cancellationToken);
    }

    public Task<StockMovementItem?> GetAsync(long transactionId, CancellationToken cancellationToken = default)
        => Project(db.StockTransactions.AsNoTracking().Where(t => t.Id == transactionId)).FirstOrDefaultAsync(cancellationToken);

    public Task<PagedResult<StockMovementItem>> GetHistoryAsync(int productId, ListQuery paging, CancellationToken cancellationToken = default)
    {
        // Posting order, not OccurredAt: QuantityAfter is the running balance in posting order (backdating is allowed).
        var rows = db.StockTransactions.AsNoTracking().Where(t => t.ProductId == productId).OrderByDescending(t => t.Id);
        return PagedResult<StockMovementItem>.CreateAsync(Project(rows), paging, cancellationToken);
    }

    public Task<PagedResult<StockMovementItem>> SearchMovementsAsync(MovementQuery query, CancellationToken cancellationToken = default)
    {
        var rows = db.StockTransactions.AsNoTracking().AsQueryable();

        if (query.ProductId is { } productId)
        {
            rows = rows.Where(t => t.ProductId == productId);
        }

        if (query.Type is { } type)
        {
            rows = rows.Where(t => t.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            var userId = query.UserId;
            rows = rows.Where(t => t.UserId == userId);
        }

        if (query.From is { } from)
        {
            rows = rows.Where(t => t.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(t => t.OccurredAt < to);
        }

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            rows = rows.Where(t =>
                EF.Functions.Like(t.Product!.Sku, pattern, LikePattern.Escape)
                || EF.Functions.Like(t.Product.Name, pattern, LikePattern.Escape)
                || (t.Reason != null && EF.Functions.Like(t.Reason, pattern, LikePattern.Escape))
                || (t.Reference != null && EF.Functions.Like(t.Reference, pattern, LikePattern.Escape)));
        }

        return PagedResult<StockMovementItem>.CreateAsync(Project(rows.OrderByDescending(t => t.Id)), query, cancellationToken);
    }

    public async Task<IReadOnlyList<MovementUser>> ListMovementUsersAsync(CancellationToken cancellationToken = default)
        => await db.Users.AsNoTracking()
            .Where(u => db.StockTransactions.Any(t => t.UserId == u.Id))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Select(u => new MovementUser(u.Id, u.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CountPreviewLine>> PreviewCountAsync(IReadOnlyList<CountEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ids = entries.Select(e => e.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.Name, p.Unit, p.QuantityOnHand })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var lines = new List<CountPreviewLine>(entries.Count);
        foreach (var entry in entries)
        {
            if (products.TryGetValue(entry.ProductId, out var p))
            {
                lines.Add(new CountPreviewLine(p.Id, p.Sku, p.Name, p.Unit, p.QuantityOnHand, entry.Counted));
            }
        }

        return lines;
    }

    public async Task<StockVerifyReport> VerifyAsync(bool fix = false, CancellationToken cancellationToken = default)
    {
        if (!fix)
        {
            return await VerifyCoreAsync(fix: false, cancellationToken);
        }

        return await InTransactionAsync(() => VerifyCoreAsync(fix: true, cancellationToken), _ => true, cancellationToken);
    }

    // ---- posting -----------------------------------------------------------------------------------------------

    /// <summary>A line after validation and normalisation.</summary>
    private sealed record Planned(
        int Index,
        StockMovement Movement,
        string? Reason,
        string? Reference,
        string? Note,
        DateTime OccurredAt);

    private async Task<Result<IReadOnlyList<PostedMovement>>> PostCoreAsync(
        List<Planned> lines, StockContext context, bool allowNegative, CancellationToken cancellationToken)
    {
        // Inside the transaction: with BEGIN IMMEDIATE no other writer can change these quantities until we finish.
        var ids = lines.Select(l => l.Movement.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.IsActive, p.QuantityOnHand })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var opening = products.ToDictionary(p => p.Key, p => p.Value.QuantityOnHand);
        var balance = new Dictionary<int, int>(opening);
        var errors = new List<Error>();
        var rows = new List<(int LineIndex, StockTransaction Row)>(lines.Count);
        var skipped = new List<PostedMovement>();

        foreach (var line in lines)
        {
            var m = line.Movement;
            if (!products.TryGetValue(m.ProductId, out var product))
            {
                errors.Add(new Error("stock.product_missing", "That part no longer exists.", StockFields.Line(line.Index, StockFields.Product)));
                continue;
            }

            if (!product.IsActive)
            {
                errors.Add(new Error("stock.product_archived", $"{product.Sku} is archived. Restore it before recording stock for it.", StockFields.Line(line.Index, StockFields.Product)));
                continue;
            }

            var current = balance[m.ProductId];
            var delta = m.Type switch
            {
                StockTransactionType.Receive or StockTransactionType.Return => m.Quantity,
                StockTransactionType.Issue => -m.Quantity,
                StockTransactionType.Adjust => m.Quantity,
                StockTransactionType.Count => m.Quantity - current,
                _ => throw new InvalidOperationException($"Unexpected type {m.Type}."), // Validate() already refused it.
            };

            if (delta == 0)
            {
                // Only a Count can get here (Validate rejects zero for the others): the shelf matches the system.
                skipped.Add(new PostedMovement(line.Index, m.ProductId, null, 0, current));
                continue;
            }

            var after = (long)current + delta;
            if (after < 0 && delta < 0 && !allowNegative)
            {
                errors.Add(InsufficientStock(product.Sku, current, after, StockFields.Line(line.Index, StockFields.Quantity)));
                continue;
            }

            if (after > MaxQuantity)
            {
                errors.Add(TooLarge(StockFields.Line(line.Index, StockFields.Quantity)));
                continue;
            }

            balance[m.ProductId] = (int)after;
            rows.Add((line.Index, new StockTransaction
            {
                ProductId = m.ProductId,
                Type = m.Type,
                QuantityDelta = delta,
                QuantityAfter = (int)after,
                UnitCostCents = m.UnitCostCents,
                Reason = line.Reason,
                Reference = line.Reference,
                Note = line.Note,
                PurchaseOrderLineId = m.PurchaseOrderLineId,
                ImportBatchId = m.ImportBatchId,
                UserId = context.UserId,
                OccurredAt = line.OccurredAt,
            }));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<IReadOnlyList<PostedMovement>>([.. errors]);
        }

        // Insert order = line order, so Id order is posting order and QuantityAfter chains correctly.
        await SaveRowsAsync(rows.Select(r => r.Row).ToList(), cancellationToken);
        await UpdateCachesAsync(opening, balance, cancellationToken);

        var posted = rows
            .Select(r => new PostedMovement(r.LineIndex, r.Row.ProductId, r.Row.Id, r.Row.QuantityDelta, r.Row.QuantityAfter))
            .Concat(skipped)
            .OrderBy(p => p.LineIndex)
            .ToList();
        return Result.Success<IReadOnlyList<PostedMovement>>(posted);
    }

    private async Task<Result<PostedMovement>> ReverseCoreAsync(
        long transactionId, StockContext context, string? note, bool allowNegative, DateTime now, bool orderReceipt, CancellationToken cancellationToken)
    {
        var original = await db.StockTransactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => new { t.Id, t.ProductId, t.Type, t.QuantityDelta, t.UnitCostCents, t.PurchaseOrderLineId, t.Reference })
            .FirstOrDefaultAsync(cancellationToken);
        if (original is null)
        {
            return Result.Failure<PostedMovement>("stock.transaction_missing", "That transaction does not exist.");
        }

        if (original.Type == StockTransactionType.Reversal)
        {
            return Result.Failure<PostedMovement>("stock.reverse_reversal", "A reversal cannot be reversed. Record a new movement instead.");
        }

        if (orderReceipt != (original.PurchaseOrderLineId is not null))
        {
            return orderReceipt
                ? Result.Failure<PostedMovement>("stock.not_order_receipt", "That transaction does not belong to a purchase order.")
                : Result.Failure<PostedMovement>("stock.reverse_order_receipt", "That is a purchase order receipt. Reverse it from the purchase order, so the order's received quantity follows.");
        }

        if (await db.StockTransactions.AnyAsync(t => t.ReversesTransactionId == transactionId, cancellationToken))
        {
            return Result.Failure<PostedMovement>(AlreadyReversed(transactionId));
        }

        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == original.ProductId)
            .Select(p => new { p.Sku, p.QuantityOnHand })
            .FirstAsync(cancellationToken);

        var delta = -original.QuantityDelta;
        var after = (long)product.QuantityOnHand + delta;
        if (after < 0 && delta < 0 && !allowNegative)
        {
            return Result.Failure<PostedMovement>(InsufficientStock(product.Sku, product.QuantityOnHand, after, field: null));
        }

        if (after > MaxQuantity)
        {
            return Result.Failure<PostedMovement>(TooLarge(field: null));
        }

        // Archived products can still be reversed (FR / business rules): only new movements need an active product.
        // PurchaseOrderLineId is copied so a line's receipts net out; PurchaseOrderService lowers the line's received quantity.
        var row = new StockTransaction
        {
            ProductId = original.ProductId,
            Type = StockTransactionType.Reversal,
            QuantityDelta = delta,
            QuantityAfter = (int)after,
            UnitCostCents = original.UnitCostCents,
            Reason = $"Reversal of #{original.Id}",
            Reference = original.Reference,
            Note = note,
            PurchaseOrderLineId = original.PurchaseOrderLineId,
            ReversesTransactionId = original.Id,
            UserId = context.UserId,
            OccurredAt = now,
        };

        try
        {
            await SaveRowsAsync([row], cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, out var target) && target.Contains("ReversesTransactionId", StringComparison.Ordinal))
        {
            return Result.Failure<PostedMovement>(AlreadyReversed(transactionId));
        }

        var before = new Dictionary<int, int> { [original.ProductId] = product.QuantityOnHand };
        var next = new Dictionary<int, int> { [original.ProductId] = (int)after };
        await UpdateCachesAsync(before, next, cancellationToken);

        return Result.Success(new PostedMovement(0, original.ProductId, row.Id, delta, (int)after));
    }

    /// <summary>Inserts the rows, then detaches them so a later <c>SaveChanges</c> on the same context never sees them again.</summary>
    private async Task SaveRowsAsync(List<StockTransaction> rows, CancellationToken cancellationToken)
    {
        db.StockTransactions.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            foreach (var row in rows)
            {
                var entry = db.Entry(row);
                if (entry.State != EntityState.Detached)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>
    /// Writes the new cached quantities with a "still the value we read" guard. Done as a direct UPDATE (not through the change
    /// tracker) on purpose: it does not bump <c>RowVersion</c>/<c>UpdatedAt</c>, so a stock movement never makes someone's open
    /// product edit form fail with a false conflict, and it cannot overwrite other columns.
    /// </summary>
    private async Task UpdateCachesAsync(IReadOnlyDictionary<int, int> opening, IReadOnlyDictionary<int, int> closing, CancellationToken cancellationToken)
    {
        foreach (var (productId, newQuantity) in closing)
        {
            var oldQuantity = opening[productId];
            if (oldQuantity == newQuantity)
            {
                continue;
            }

            var affected = await db.Products
                .Where(p => p.Id == productId && p.QuantityOnHand == oldQuantity)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.QuantityOnHand, newQuantity), cancellationToken);

            if (affected != 1)
            {
                // Cannot happen under BEGIN IMMEDIATE unless someone edited the database by hand mid-flight. Abort (rolls back).
                logger.LogError("Stock cache for product {ProductId} changed during posting (expected {Expected}).", productId, oldQuantity);
                throw new InvalidOperationException($"Stock for product {productId} changed while posting. Nothing was saved; try again, and run the stock verification if it persists.");
            }
        }
    }

    // ---- verify ------------------------------------------------------------------------------------------------

    private async Task<StockVerifyReport> VerifyCoreAsync(bool fix, CancellationToken cancellationToken)
    {
        var checkedCount = await db.Products.CountAsync(cancellationToken);

        var rows = await db.Products.AsNoTracking()
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Name,
                Cached = p.QuantityOnHand,
                Ledger = db.StockTransactions.Where(t => t.ProductId == p.Id).Sum(t => (int?)t.QuantityDelta) ?? 0,
            })
            .Where(x => x.Cached != x.Ledger)
            .OrderBy(x => x.Sku)
            .ToListAsync(cancellationToken);

        var mismatches = rows.Select(x => new StockMismatch(x.Id, x.Sku, x.Name, x.Cached, x.Ledger)).ToList();

        if (fix)
        {
            foreach (var mismatch in mismatches)
            {
                await db.Products.Where(p => p.Id == mismatch.ProductId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.QuantityOnHand, mismatch.Ledger), cancellationToken);
                logger.LogWarning(
                    "Stock verification repaired {Sku}: cached {Cached} -> ledger {Ledger}.", mismatch.Sku, mismatch.Cached, mismatch.Ledger);
            }
        }

        return new StockVerifyReport(checkedCount, mismatches, Fixed: fix && mismatches.Count > 0);
    }

    // ---- validation --------------------------------------------------------------------------------------------

    private static Planned Validate(int index, StockMovement m, DateTime now, List<Error> errors)
    {
        string Field(string name) => StockFields.Line(index, name);

        if (m.Type is not (StockTransactionType.Receive or StockTransactionType.Issue or StockTransactionType.Adjust
            or StockTransactionType.Count or StockTransactionType.Return))
        {
            errors.Add(new Error("stock.type_invalid", "Choose a movement type (a reversal is made from the history list).", Field(StockFields.Type)));
        }
        else
        {
            switch (m.Type)
            {
                case StockTransactionType.Adjust when m.Quantity == 0:
                    errors.Add(new Error("stock.quantity_zero", "Enter how many to add (positive) or remove (negative).", Field(StockFields.Quantity)));
                    break;
                case StockTransactionType.Count when m.Quantity < 0:
                    errors.Add(new Error("stock.count_negative", "A counted quantity cannot be negative.", Field(StockFields.Quantity)));
                    break;
                case StockTransactionType.Receive or StockTransactionType.Issue or StockTransactionType.Return when m.Quantity <= 0:
                    errors.Add(new Error("stock.quantity_positive", "Quantity must be greater than zero.", Field(StockFields.Quantity)));
                    break;
            }

            if (Math.Abs((long)m.Quantity) > MaxQuantity)
            {
                errors.Add(TooLarge(Field(StockFields.Quantity)));
            }
        }

        var reason = Clean(m.Reason);
        if (m.Type == StockTransactionType.Adjust && reason is null)
        {
            errors.Add(new Error("stock.reason_required", "A reason is required for an adjustment (for example: damaged, lost, found).", Field(StockFields.Reason)));
        }

        TooLong(errors, reason, StockTransaction.ReasonMaxLength, Field(StockFields.Reason), "Reason");
        var reference = Clean(m.Reference);
        TooLong(errors, reference, StockTransaction.ReferenceMaxLength, Field(StockFields.Reference), "Reference");
        var note = Clean(m.Note);
        TooLong(errors, note, StockTransaction.NoteMaxLength, Field(StockFields.Note), "Note");

        if (m.UnitCostCents is < 0)
        {
            errors.Add(new Error("stock.cost_negative", "Unit cost cannot be negative.", Field(StockFields.UnitCost)));
        }

        var occurredAt = m.OccurredAt is { } given ? ToUtc(given) : now;
        if (occurredAt > now + FutureTolerance)
        {
            errors.Add(new Error("stock.future_date", "The date cannot be in the future.", Field(StockFields.OccurredAt)));
        }

        return new Planned(index, m, reason, reference, note, occurredAt > now ? now : occurredAt);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void TooLong(List<Error> errors, string? value, int max, string field, string label)
    {
        if (value is { } text && text.Length > max)
        {
            errors.Add(new Error($"stock.{label.ToLowerInvariant()}_too_long", $"{label} can be at most {max} characters.", field));
        }
    }

    private static Error InsufficientStock(string sku, int current, long after, string? field) => new(
        "stock.insufficient",
        current <= 0
            ? $"{sku}: none on hand, so this would leave {after}."
            : $"{sku}: only {current} on hand, so this would leave {after}.",
        field);

    private static Error TooLarge(string? field)
        => new("stock.too_large", $"Quantities cannot exceed {MaxQuantity:N0}.", field);

    private static Error AlreadyReversed(long id)
        => new("stock.already_reversed", $"Transaction #{id} has already been reversed.");

    private static void RequireUser(StockContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.UserId))
        {
            throw new ArgumentException("A stock movement needs the id of the user who made it.", nameof(context));
        }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (SQLite <c>BEGIN IMMEDIATE</c>: the write lock is taken up
    /// front, so the quantities read inside cannot change before they are written). When the caller already has a transaction
    /// open it is reused and left for the caller to commit.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, Func<T, bool> shouldCommit, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await work();
        if (shouldCommit(result))
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result; // Disposing an uncommitted transaction rolls it back.
    }

    private IQueryable<StockMovementItem> Project(IQueryable<StockTransaction> rows) => rows.Select(t => new StockMovementItem(
        t.Id,
        t.ProductId,
        t.Product!.Sku,
        t.Product.Name,
        t.Type,
        t.QuantityDelta,
        t.QuantityAfter,
        t.UnitCostCents,
        t.Reason,
        t.Reference,
        t.Note,
        t.PurchaseOrderLineId,
        t.ReversesTransactionId,
        db.StockTransactions.Where(r => r.ReversesTransactionId == t.Id).Select(r => (long?)r.Id).FirstOrDefault(),
        t.User!.DisplayName,
        t.OccurredAt,
        t.CreatedAt,
        t.PurchaseOrderLine == null ? (int?)null : t.PurchaseOrderLine.PurchaseOrderId,
        t.PurchaseOrderLine == null ? null : t.PurchaseOrderLine.PurchaseOrder!.Number));
}
