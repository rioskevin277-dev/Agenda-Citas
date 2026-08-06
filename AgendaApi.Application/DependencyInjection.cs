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
        services.AddScoped<ConfirmAppointmentUseCase>();
        services.AddScoped<SyncExternalChangesUseCase>();
        services.AddScoped<SendRemindersUseCase>();
        services.AddScoped<ListAppointmentsUseCase>();
        services.AddScoped<RenewCalendarSubscriptionsUseCase>();

        // Background services
        services.AddHostedService<ReminderBackgroundService>();
        services.AddHostedService<SubscriptionRenewalService>();

        return services;
    }
}
