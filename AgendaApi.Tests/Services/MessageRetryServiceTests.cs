using System;
using System.Linq;
using AgendaApi.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AgendaApi.Tests.Services;

/// <summary>
/// Prueba la política de reintentos con backoff del MessageRetryService (mismo esquema que
/// AdamApi): 1º→30s, 2º→2m, 3º→8m, máx 3 reintentos y expiración total de 30 min. Garantiza
/// que un fallo transitorio NO pierda el mensaje del cliente y que al agotarse el margen se
/// descarte de forma controlada (nunca en loop infinito).
/// </summary>
public class MessageRetryServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime ReceivedAt = DateTime.UtcNow;

    [Fact]
    public void Schedule_FirstFailure_DueAfterBackoff30s()
    {
        var svc = new MessageRetryService();
        var now = DateTime.UtcNow;

        svc.Schedule("573223697115", "Hola", Tenant, "Ana", ReceivedAt, now, failedAttempts: 1)
            .Should().BeTrue();

        // No vence en el momento, ni antes de 30s.
        svc.CollectDue(now).Should().BeEmpty();
        svc.CollectDue(now.AddMilliseconds(30_000 - 1)).Should().BeEmpty();

        // Vence a partir de ~30s, con el intento 2.
        var due = svc.CollectDue(now.AddMilliseconds(30_000 + 1));
        var item = due.Single();
        item.Attempt.Should().Be(2);
        item.Expired.Should().BeFalse();
        item.Content.Should().Be("Hola");
        item.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public void Schedule_EsclatingBackoff_SecondIs2m()
    {
        var svc = new MessageRetryService();
        var now = DateTime.UtcNow;

        // 1º intento fallado ⇒ 30s.
        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, failedAttempts: 1).Should().BeTrue();
        var first = svc.CollectDue(now.AddMinutes(10)).Single();

        // 2º intento fallado ⇒ 2m (el siguiente es el intento 3).
        svc.Schedule(first.Key, first.Content, first.TenantId, first.ClientName, first.ReceivedAt, now, failedAttempts: first.Attempt)
            .Should().BeTrue();
        svc.CollectDue(now.AddMinutes(1)).Should().BeEmpty(); // 1m < 2m
        var second = svc.CollectDue(now.AddMinutes(3)).Single();
        second.Attempt.Should().Be(3);
    }

    [Fact]
    public void Schedule_ExceedingMaxRetries_Dropped()
    {
        var svc = new MessageRetryService(maxRetries: 3);
        var now = DateTime.UtcNow;

        // 3 fallos agendan intentos 2, 3 y 4; el cuarto fallo ya no debe programar nada.
        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, 1).Should().BeTrue();
        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, 2).Should().BeTrue();
        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, 3).Should().BeTrue();
        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, 4).Should().BeFalse();
        // Inválidos: 0 o negativos nunca agendan.
        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, 0).Should().BeFalse();
    }

    [Fact]
    public void CollectDue_AfterExpiration_FlagsExpired()
    {
        var svc = new MessageRetryService(totalExpiration: TimeSpan.FromMinutes(30));
        var received = DateTime.UtcNow.AddMinutes(-31); // ya fuera de la ventana de 30 min
        var now = DateTime.UtcNow;

        svc.Schedule("phone", "m", Tenant, null, received, now, 1).Should().BeTrue();

        var due = svc.CollectDue(now.AddSeconds(31)); // vence por backoff, pero el plazo total se agotó
        due.Single().Expired.Should().BeTrue();
    }

    [Fact]
    public void CollectDue_IsIdempotent_ItemDeliveredOnce()
    {
        var svc = new MessageRetryService();
        var now = DateTime.UtcNow;

        svc.Schedule("phone", "m", Tenant, null, ReceivedAt, now, 1).Should().BeTrue();

        svc.CollectDue(now.AddSeconds(31)).Should().HaveCount(1);
        // No queda nada pendiente ni se entrega de nuevo.
        svc.CollectDue(now.AddMinutes(10)).Should().BeEmpty();
        svc.Pending.Should().BeEmpty();
    }
}