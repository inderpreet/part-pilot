namespace InventoryTracker.Services;

public static class ProjectServiceCollectionExtensions
{
    /// <summary>Registers the project service and its event publisher (scoped: share the request's DbContext and transaction).</summary>
    public static IServiceCollection AddProjects(this IServiceCollection services)
    {
        services.AddScoped<ProjectEventPublisher>();
        services.AddScoped<IProjectService, ProjectService>();
        return services;
    }
}
