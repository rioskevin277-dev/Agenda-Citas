using System.Text.Json;
using AgendaApi.Api.Controllers;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgendaApi.Tests.Controllers;

/// <summary>
/// Gate del endpoint GET api/v1/dashboard/failures (causas de turnos perdidos): misma validación
/// por clave que /conversations, límite acotado y salida newest-first tal como la entrega el repo.
/// </summary>
public class LiveConversationsControllerTests
{
    private const string ValidKey = "clave-de-test";

    private readonly Mock<IConversationHistoryRepository> _conversationRepo = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<ITurnFailureRepository> _turnFailureRepo = new();

    private LiveConversationsController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:Key"] = ValidKey
            })
            .Build();

        var env = Mock.Of<IWebHostEnvironment>(e => e.WebRootPath == "");

        return new LiveConversationsController(
            _conversationRepo.Object,
            _tenantRepo.Object,
            _turnFailureRepo.Object,
            configuration,
            env,
            NullLogger<LiveConversationsController>.Instance);
    }

    private static TurnFailure Failure(string motivo, DateTime? fecha = null) => new()
    {
        IdTurnFailure = Guid.NewGuid(),
        IdTenant = Guid.NewGuid(),
        PhoneCliente = "+573001112233",
        Motivo = motivo,
        Detalle = "intentos=6; OpenRouter: boom | Groq: boom",
        FechaCreacion = fecha ?? DateTime.UtcNow
    };

    [Fact]
    public async Task Failures_InvalidKey_Returns401()
    {
        var result = await CreateController().Failures("clave-incorrecta");

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _turnFailureRepo.Verify(r => r.GetLatestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failures_MissingKey_Returns401()
    {
        var result = await CreateController().Failures("");

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Failures_ValidKey_ReturnsFailuresNewestFirst()
    {
        var newest = Failure("timeout", DateTime.UtcNow.AddMinutes(-1));
        var older = Failure("all_providers_failed", DateTime.UtcNow.AddMinutes(-5));
        // El repo ya devuelve más recientes primero; el controlador debe preservar ese orden.
        _turnFailureRepo
            .Setup(r => r.GetLatestAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TurnFailure> { newest, older });

        var result = await CreateController().Failures(ValidKey);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value!);
        using var doc = JsonDocument.Parse(json);
        var failures = doc.RootElement.GetProperty("failures");
        failures.GetArrayLength().Should().Be(2);
        failures[0].GetProperty("id").GetGuid().Should().Be(newest.IdTurnFailure);
        failures[0].GetProperty("motivo").GetString().Should().Be("timeout");
        failures[1].GetProperty("motivo").GetString().Should().Be("all_providers_failed");
        failures[1].GetProperty("phone").GetString().Should().Be("+573001112233");
        failures[1].GetProperty("detalle").GetString().Should().Contain("Groq");
    }

    [Fact]
    public async Task Failures_LimitClampedTo200_AndDefaultsTo100()
    {
        _turnFailureRepo
            .Setup(r => r.GetLatestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TurnFailure>());

        var controller = CreateController();
        await controller.Failures(ValidKey, limit: 5000);
        _turnFailureRepo.Verify(r => r.GetLatestAsync(200, It.IsAny<CancellationToken>()), Times.Once);

        await controller.Failures(ValidKey);
        _turnFailureRepo.Verify(r => r.GetLatestAsync(100, It.IsAny<CancellationToken>()), Times.Once);
    }
}
