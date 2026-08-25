using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.AiProviders;
using AgendaApi.Infrastructure.CalendarProviders;
using AgendaApi.Infrastructure.Data;
using AgendaApi.Infrastructure.Messaging;
using AgendaApi.Infrastructure.Middleware;
using AgendaApi.Infrastructure.Repositories;
using AgendaApi.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgendaApi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureLayer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Database
        var connectionString = configuration.GetConnectionString("AgendaDb");
        services.AddDbContext<AgendaDbContext>(options =>
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(3);
            }));

        // Unit of Work
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Repositories
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ICalendarConnectionRepository, CalendarConnectionRepository>();
        services.AddScoped<IAppointmentRepository, AppointmentRepository>();
        services.AddScoped<IClientRepository, ClientRepository>();
        services.AddScoped<IServiceTypeRepository, ServiceTypeRepository>();
        services.AddScoped<IProfessionalRepository, ProfessionalRepository>();
        services.AddScoped<IAvailabilityRepository, AvailabilityRepository>();
        services.AddScoped<IReminderLogRepository, ReminderLogRepository>();
        services.AddScoped<IWaitlistEntryRepository, WaitlistEntryRepository>();

        // Tenant Context (scoped)
        services.AddScoped<ITenantContext, TenantContext>();

        // Token Encryption
        services.AddSingleton<ITokenEncryptionService, TokenEncryptionService>();

        // Calendar Providers (with IHttpClientFactory)
        // Usamos named clients (no typed): los adaptadores reciben IHttpClientFactory
        // en el constructor, no HttpClient. Registrarlos como typed clients rompía
        // la resolución por DI (constructor no compatible) — el adaptador de Google
        // nunca se construía y GetAvailabilityAsync caía en "usando datos locales".
        services.AddHttpClient("google-calendar", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("microsoft-graph", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<GoogleCalendarAdapter>();
        services.AddScoped<MicrosoftGraphCalendarAdapter>();
        services.AddScoped<ICalendarProvider, GoogleCalendarAdapter>(sp =>
            sp.GetRequiredService<GoogleCalendarAdapter>());
        services.AddScoped<ICalendarProvider, MicrosoftGraphCalendarAdapter>(sp =>
            sp.GetRequiredService<MicrosoftGraphCalendarAdapter>());
        services.AddScoped<ICalendarProviderFactory, CalendarProviderFactory>();

        // AI Providers
        // Usamos named clients (no typed) porque los proveedores reciben
        // IHttpClientFactory en el constructor, no HttpClient. Registrarlos
        // como typed clients rompía la resolución por DI (constructor no compatible).
        services.AddHttpClient("groq-api", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", "AgendaApi/1.0");
        });
        services.AddScoped<GroqProvider>();
        // Speech-to-text (audios del cliente) con Groq Whisper — reutiliza el client "groq-api".
        services.AddScoped<ISpeechToTextProvider, GroqSpeechToTextProvider>();

        // Respaldo gratuito de Groq: OpenRouter (gateway OpenAI-compatible con modelos :free).
        services.AddHttpClient("openrouter-api", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", "AgendaApi/1.0");
        });
        services.AddScoped<OpenRouterProvider>();

        services.AddHttpClient("openai-api", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", "AgendaApi/1.0");
        });
        services.AddScoped<OpenAIProvider>();

        services.AddHttpClient("anthropic-api", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", "AgendaApi/1.0");
        });
        services.AddScoped<AnthropicProvider>();

        // WhatsApp Provider
        services.AddHttpClient<WhatsAppCloudApiAdapter>("whatsapp-api", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "AgendaApi/1.0");
        });

        services.AddScoped<IMessagingProvider, WhatsAppCloudApiAdapter>();

        // Message Buffer + Chat Orchestrator
        services.AddSingleton<MessageBufferService>();
        services.AddHostedService<MessageBufferService>(sp => sp.GetRequiredService<MessageBufferService>());

        // Memoria de conversación (contexto entre mensajes + sesión activa de WhatsApp)
        services.AddSingleton<ConversationMemoryService>();
        services.AddSingleton<IConversationSessionService>(sp => sp.GetRequiredService<ConversationMemoryService>());

        // Estado estructurado por conversación (reserva en curso)
        services.AddSingleton<ConversationStateService>();

        // CRM: memoria operativa del cliente para ADAM (contexto del cliente para la IA)
        services.AddScoped<ClientContextService>();
        services.AddScoped<IConversationHistoryRepository, ConversationHistoryRepository>();
        services.AddScoped<ITurnFailureRepository, TurnFailureRepository>();

        services.AddScoped<ChatOrchestratorService>();

        return services;
    }
}
