using AgendaApi.Application.Rules;
using AgendaApi.Application.Services;
using AgendaApi.Application.UseCases;
using AgendaApi.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaApi.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationLayer(this IServiceCollection services)
    {
        // Motor de reglas de agenda
        services.AddScoped<IBookingPolicy, BookingPolicy>();

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
        services.AddScoped<RepairExternalCalendarSyncUseCase>();
        services.AddScoped<WaitlistNotificationUseCase>();
        services.AddScoped<IWaitlistNotifier>(sp => sp.GetRequiredService<WaitlistNotificationUseCase>());
        services.AddScoped<GetDashboardStatsUseCase>();
        services.AddScoped<HandoffExpirationUseCase>();

        // Background services
        services.AddHostedService<ReminderBackgroundService>();
        services.AddHostedService<SubscriptionRenewalService>();
        services.AddHostedService<ExternalSyncRepairBackgroundService>();
        services.AddHostedService<WaitlistNotificationBackgroundService>();
        services.AddHostedService<HandoffExpirationBackgroundService>();

        return services;
    }
}
