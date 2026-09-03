using AgendaApi.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgendaApi.Tests.Services;

/// <summary>
/// Dirty flag por-tenant (RF3): el webhook de cancelación externa marca el tenant como "sucio"
/// para que el orquestador fuerce el re-check de disponibilidad en el siguiente turno, aunque no
/// haya PendingBooking ni pedido de fecha/hora. Semántica one-shot: consumir el flag lo limpia.
/// </summary>
public class ConversationStateServiceTests
{
    private static ConversationStateService Build()
        => new(NullLogger<ConversationStateService>.Instance);

    [Fact]
    public void MarkTenantDirty_IsIdempotent_AlwaysRetainsMark()
    {
        var sut = Build();
        var tenantId = Guid.NewGuid();

        sut.MarkTenantDirty(tenantId);
        sut.MarkTenantDirty(tenantId); // segunda marca no debe limpiar el flag

        sut.ConsumeTenantDirty(tenantId).Should().BeTrue("marcar dos veces la misma tenant sigue dejándola sucia");
    }

    [Fact]
    public void ConsumeTenantDirty_IsOneShot_ReturnsTrueThenClears()
    {
        var sut = Build();
        var tenantId = Guid.NewGuid();

        sut.MarkTenantDirty(tenantId);

        var firstRead = sut.ConsumeTenantDirty(tenantId);
        var secondRead = sut.ConsumeTenantDirty(tenantId);

        firstRead.Should().BeTrue("el flag estaba marcado");
        secondRead.Should().BeFalse("consumir el flag lo limpia: la segunda lectura es false");
    }

    [Fact]
    public void ConsumeTenantDirty_WhenNeverMarked_ReturnsFalse()
    {
        var sut = Build();
        var tenantId = Guid.NewGuid();

        sut.ConsumeTenantDirty(tenantId).Should().BeFalse("una tenant nunca marcada no está sucia");
    }
}
