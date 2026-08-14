using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Services;
using FluentAssertions;

namespace AgendaApi.Tests.Services;

public class ClientStateCalculatorTests
{
    private const string WhatsApp = "573216403049";
    private static readonly Guid TenantId = Guid.NewGuid();

    private static Client Client(string estado = "nuevo", DateTime? ultima = null) => new()
    {
        IdClient = Guid.NewGuid(),
        IdTenant = TenantId,
        WhatsApp = WhatsApp,
        Estado = estado,
        UltimaInteraccion = ultima ?? DateTime.UtcNow
    };

    private static Appointment Appointment(string estado, DateTime inicio) => new()
    {
        IdAppointment = Guid.NewGuid(),
        IdTenant = TenantId,
        IdClient = Guid.NewGuid(),
        Estado = estado,
        FechaInicio = inicio,
        FechaFin = inicio.AddMinutes(30)
    };

    [Fact]
    public void ApplyDerivedState_NullAppointments_StaysNuevoNoProxima()
    {
        var client = Client();

        ClientStateCalculator.ApplyDerivedState(client, null, DateTime.UtcNow);

        client.Estado.Should().Be("nuevo");
        client.ProximaCita.Should().BeNull();
    }

    [Fact]
    public void ApplyDerivedState_NoShows_SetEstadoNoShow()
    {
        var client = Client();
        var now = DateTime.UtcNow;

        ClientStateCalculator.ApplyDerivedState(client, new[]
        {
            Appointment("no_show", now.AddDays(-10)),
            Appointment("no_show", now.AddDays(-3)),
            Appointment("completed", now.AddDays(-30))
        }, now);

        client.Estado.Should().Be("no_show");
    }

    [Fact]
    public void ApplyDerivedState_FrequentClient_SetEstadoFrecuente()
    {
        var client = Client();
        var now = DateTime.UtcNow;

        ClientStateCalculator.ApplyDerivedState(client, new[]
        {
            Appointment("completed", now.AddDays(-30)),
            Appointment("completed", now.AddDays(-20)),
            Appointment("completed", now.AddDays(-10)),
            Appointment("pending", now.AddDays(5))
        }, now);

        client.Estado.Should().Be("frecuente");
    }

    [Fact]
    public void ApplyDerivedState_Inactive_SetEstadoInactivo()
    {
        var client = Client(ultima: DateTime.UtcNow.AddDays(-120));

        ClientStateCalculator.ApplyDerivedState(client, new List<Appointment>(), DateTime.UtcNow);

        client.Estado.Should().Be("inactivo");
    }

    [Fact]
    public void ApplyDerivedState_VipManualState_IsNotOverwritten()
    {
        var client = Client(estado: "vip");
        var now = DateTime.UtcNow;

        ClientStateCalculator.ApplyDerivedState(client, new[]
        {
            Appointment("no_show", now.AddDays(-5)),
            Appointment("no_show", now.AddDays(-2))
        }, now);

        client.Estado.Should().Be("vip");
    }

    [Fact]
    public void ApplyDerivedState_FuturePending_SetsProximaCita()
    {
        var client = Client();
        var now = DateTime.UtcNow;
        var futura = now.AddDays(7);

        ClientStateCalculator.ApplyDerivedState(client, new[]
        {
            Appointment("pending", futura),
            Appointment("completed", now.AddDays(-30))
        }, now);

        client.ProximaCita.Should().NotBeNull();
        client.ProximaCita.Should().BeCloseTo(futura, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ApplyDerivedState_NoFutureAppointments_NullProximaCita()
    {
        var client = Client();
        var now = DateTime.UtcNow;

        ClientStateCalculator.ApplyDerivedState(client, new[]
        {
            Appointment("completed", now.AddDays(-10)),
            Appointment("cancelled", now.AddDays(-5))
        }, now);

        client.ProximaCita.Should().BeNull();
    }

    [Fact]
    public void TraducirEstado_KnownStates_ReturnsHumanReadable()
    {
        ClientStateCalculator.TraducirEstado("frecuente").Should().Contain("frecuente");
        ClientStateCalculator.TraducirEstado("no_show").Should().Contain("inasistencias");
        ClientStateCalculator.TraducirEstado("vip").Should().Contain("VIP");
    }
}