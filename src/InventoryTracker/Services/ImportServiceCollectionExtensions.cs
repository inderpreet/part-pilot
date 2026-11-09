namespace InventoryTracker.Services;

public static class ImportServiceCollectionExtensions
{
    public static IServiceCollection AddDataTransfer(this IServiceCollection services)
    {
        services.AddSingleton<ImportStaging>();
        services.AddScoped<ImportEventPublisher>();
        services.AddScoped<BackupEventPublisher>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<IBackupService, BackupService>();
        return services;
    }
}
