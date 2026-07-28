using AgendaApi.Application.Services;
using AgendaApi.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaApi.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationLayer(this IServiceCollection services)
    {
        // Use cases
        services.AddScoped<CheckAvailabilityUseCase>();
        services.AddScoped<CreateAppointmentUseCase>();
        services.AddScoped<CancelAppointmentUseCase>();
        services.AddScoped<RescheduleAppointmentUseCase>();
        services.AddScoped<SyncExternalChangesUseCase>();
        services.AddScoped<SendRemindersUseCase>();
        services.AddScoped<ListAppointmentsUseCase>();

        // Background services
        services.AddHostedService<ReminderBackgroundService>();

        return services;
    }
}
