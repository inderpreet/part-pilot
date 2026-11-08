using System.Globalization;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>Typed access to <see cref="AppSetting"/> values with defaults and an in-memory cache.</summary>
public interface ISettingsService
{
    Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken = default);

    Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<Result> SetAsync(string key, bool value, CancellationToken cancellationToken = default);

    Task<Result> SetAsync(string key, int value, CancellationToken cancellationToken = default);

    Task<BusinessSettings> GetBusinessSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and saves all business settings at once (all or nothing).</summary>
    Task<Result<BusinessSettings>> UpdateBusinessSettingsAsync(BusinessSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>The settings an Admin edits on the Settings page (F01 FR-7).</summary>
public sealed record BusinessSettings(
    string CompanyName,
    string CurrencyCode,
    string CurrencySymbol,
    bool AllowNegativeStock,
    string PurchaseOrderNumberPrefix,
    string ProjectNumberPrefix = "PRJ-")
{
    public const int CompanyNameMaxLength = 100;
    public const int CurrencySymbolMaxLength = 5;
    public const int PurchaseOrderPrefixMaxLength = 10;
    public const int ProjectPrefixMaxLength = 10;

    // Field names match the property names so pages can attach errors to inputs.
    public static class Fields
    {
        public const string CompanyName = nameof(CompanyName);
        public const string CurrencyCode = nameof(CurrencyCode);
        public const string CurrencySymbol = nameof(CurrencySymbol);
        public const string PurchaseOrderNumberPrefix = nameof(PurchaseOrderNumberPrefix);
        public const string ProjectNumberPrefix = nameof(ProjectNumberPrefix);
    }
}

/// <summary>
/// Registered as a singleton; opens its own DbContext scope per database access. The cache holds the full
/// key/value snapshot and is dropped on every write.
/// <para>
/// A company name that is still the seeded placeholder ("My Company") means "not chosen yet", so the configured
/// default (<c>Branding:CompanyName</c>) is shown instead; any name saved in Admin → Settings wins.
/// </para>
/// </summary>
public sealed class SettingsService(IServiceScopeFactory scopeFactory, BrandingSettings? branding = null) : ISettingsService
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile IReadOnlyDictionary<string, string>? _cache;

    public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureKnown(key);
        var all = await GetAllAsync(cancellationToken);
        return all[key];
    }

    public async Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken = default)
    {
        var text = await GetStringAsync(key, cancellationToken);
        return bool.TryParse(text, out var value) ? value : bool.Parse(SettingKeys.Defaults[key]);
    }

    public async Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default)
    {
        var text = await GetStringAsync(key, cancellationToken);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.Parse(SettingKeys.Defaults[key], CultureInfo.InvariantCulture);
    }

    /// <summary>All known settings: stored values layered over defaults.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } loaded)
            {
                return loaded;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

            var merged = new Dictionary<string, string>(SettingKeys.Defaults, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in stored)
            {
                merged[key] = value;
            }

            if (branding is not null
                && string.Equals(merged[SettingKeys.CompanyName], SettingKeys.Defaults[SettingKeys.CompanyName], StringComparison.Ordinal))
            {
                merged[SettingKeys.CompanyName] = branding.CompanyName;
            }

            _cache = merged;
            return merged;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<Result> SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (!SettingKeys.Defaults.ContainsKey(key))
        {
            return Result.Failure("setting.unknown", $"Unknown setting '{key}'.", key);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.AppSettings.FindAsync([key], cancellationToken);
        if (existing is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
        _cache = null;
        return Result.Success();
    }

    public Task<Result> SetAsync(string key, bool value, CancellationToken cancellationToken = default)
        => SetAsync(key, value ? "true" : "false", cancellationToken);

    public Task<Result> SetAsync(string key, int value, CancellationToken cancellationToken = default)
        => SetAsync(key, value.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public async Task<BusinessSettings> GetBusinessSettingsAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        return new BusinessSettings(
            all[SettingKeys.CompanyName],
            all[SettingKeys.CurrencyCode],
            all[SettingKeys.CurrencySymbol],
            bool.TryParse(all[SettingKeys.StockAllowNegative], out var allow) && allow,
            all[SettingKeys.PurchaseOrderNumberPrefix],
            all[SettingKeys.ProjectNumberPrefix]);
    }

    public async Task<Result<BusinessSettings>> UpdateBusinessSettingsAsync(BusinessSettings settings, CancellationToken cancellationToken = default)
    {
        var company = settings.CompanyName?.Trim() ?? string.Empty;
        var code = settings.CurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var symbol = settings.CurrencySymbol?.Trim() ?? string.Empty;
        var prefix = settings.PurchaseOrderNumberPrefix?.Trim() ?? string.Empty;
        var projectPrefix = settings.ProjectNumberPrefix?.Trim() ?? string.Empty;

        var errors = new List<Error>();
        if (company.Length == 0)
        {
            errors.Add(new Error("settings.company_required", "Company name is required.", BusinessSettings.Fields.CompanyName));
        }
        else if (company.Length > BusinessSettings.CompanyNameMaxLength)
        {
            errors.Add(new Error("settings.company_too_long", $"Company name must be at most {BusinessSettings.CompanyNameMaxLength} characters.", BusinessSettings.Fields.CompanyName));
        }

        if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z'))
        {
            errors.Add(new Error("settings.currency_code_invalid", "Currency code must be exactly 3 letters, e.g. USD.", BusinessSettings.Fields.CurrencyCode));
        }

        if (symbol.Length == 0)
        {
            errors.Add(new Error("settings.currency_symbol_required", "Currency symbol is required.", BusinessSettings.Fields.CurrencySymbol));
        }
        else if (symbol.Length > BusinessSettings.CurrencySymbolMaxLength)
        {
            errors.Add(new Error("settings.currency_symbol_too_long", $"Currency symbol must be at most {BusinessSettings.CurrencySymbolMaxLength} characters.", BusinessSettings.Fields.CurrencySymbol));
        }

        if (prefix.Length > BusinessSettings.PurchaseOrderPrefixMaxLength)
        {
            errors.Add(new Error("settings.po_prefix_too_long", $"PO number prefix must be at most {BusinessSettings.PurchaseOrderPrefixMaxLength} characters.", BusinessSettings.Fields.PurchaseOrderNumberPrefix));
        }
        else if (prefix.Any(char.IsWhiteSpace) || prefix.Any(char.IsControl))
        {
            errors.Add(new Error("settings.po_prefix_invalid", "PO number prefix cannot contain spaces.", BusinessSettings.Fields.PurchaseOrderNumberPrefix));
        }

        if (projectPrefix.Length > BusinessSettings.ProjectPrefixMaxLength)
        {
            errors.Add(new Error("settings.project_prefix_too_long", $"Project number prefix must be at most {BusinessSettings.ProjectPrefixMaxLength} characters.", BusinessSettings.Fields.ProjectNumberPrefix));
        }
        else if (projectPrefix.Any(char.IsWhiteSpace) || projectPrefix.Any(char.IsControl))
        {
            errors.Add(new Error("settings.project_prefix_invalid", "Project number prefix cannot contain spaces.", BusinessSettings.Fields.ProjectNumberPrefix));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<BusinessSettings>([.. errors]);
        }

        var values = new Dictionary<string, string>
        {
            [SettingKeys.CompanyName] = company,
            [SettingKeys.CurrencyCode] = code,
            [SettingKeys.CurrencySymbol] = symbol,
            [SettingKeys.StockAllowNegative] = settings.AllowNegativeStock ? "true" : "false",
            [SettingKeys.PurchaseOrderNumberPrefix] = prefix,
            [SettingKeys.ProjectNumberPrefix] = projectPrefix,
        };

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.AppSettings.Where(s => values.Keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
            foreach (var (key, value) in values)
            {
                if (existing.TryGetValue(key, out var row))
                {
                    row.Value = value;
                }
                else
                {
                    db.AppSettings.Add(new AppSetting { Key = key, Value = value });
                }
            }

            await db.SaveChangesAsync(cancellationToken); // one SaveChanges = one transaction
        }

        _cache = null;
        return Result.Success(new BusinessSettings(company, code, symbol, settings.AllowNegativeStock, prefix, projectPrefix));
    }

    private static void EnsureKnown(string key)
    {
        if (!SettingKeys.Defaults.ContainsKey(key))
        {
            throw new ArgumentException($"Unknown setting '{key}'.", nameof(key));
        }
    }
}
