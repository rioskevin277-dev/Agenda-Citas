using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgendaApi.Tests.Messaging;

public class WhatsAppCloudApiAdapterTests
{
    /// <summary>
    /// Regresión: Graph devuelve los metadatos de media en snake_case ("url", "mime_type",
    /// "file_size"). La deserialización por defecto de System.Text.Json es case-sensitive, así
    /// que sin los JsonPropertyName el campo Url quedaba null y la descarga fallaba con
    /// "No se pudo obtener URL del media". Este test garantiza que el mapeo no se rompa.
    /// </summary>
    [Fact]
    public async Task DownloadMediaAsync_WithSnakeCaseMetadata_ReturnsAudioBytes()
    {
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "test-token");
        var handler = new MediaDownloadHandler();

        var context = new Mock<ITenantContext>();
        context.Setup(c => c.IsSet).Returns(true);
        context.Setup(c => c.WhatsAppAccessToken).Returns("test-token");

        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            context.Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var bytes = await adapter.DownloadMediaAsync("1539873544849939", CancellationToken.None);

        bytes.Should().NotBeNull();
        bytes.Length.Should().BeGreaterThan(0);
        // La descarga usó el "url" deserializado correctamente desde el snake_case de Graph.
        handler.LastDownloadUrl.Should().Contain("lookaside");
    }

    [Fact]
    public async Task ParseWebhookPayload_NormalE164From_UsesFrom()
    {
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(new MediaDownloadHandler()),
            new Mock<ITenantContext>().Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var body = BuildWebhookPayload(
            from: "573223697115",
            contactWaId: "573223697115");

        var msgs = await adapter.ParseWebhookPayloadAsync(body);

        msgs.Should().HaveCount(1);
        msgs[0].From.Should().Be("573223697115");
    }

    [Fact]
    public async Task SendTextAsync_E164Recipient_SendsViaWhatsApp()
    {
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "test-token");
        var context = new Mock<ITenantContext>();
        context.Setup(c => c.IsSet).Returns(true);
        context.Setup(c => c.PhoneNumberId).Returns("123456789");
        context.Setup(c => c.WhatsAppAccessToken).Returns("test-token");

        var handler = new SendCaptureHandler();
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            context.Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var result = await adapter.SendTextAsync("573223697115", "Hola", CancellationToken.None);

        handler.Sent.Should().BeTrue();
        handler.RequestBody.Should().Contain("\"messaging_product\":\"whatsapp\"");
        handler.RequestBody.Should().Contain("\"to\":\"573223697115\"");
        result.Should().NotBeNull();
    }

    /// <summary>
    /// Regresión "responde a unos y a otros no": Meta reporta los fallos de entrega reales
    /// (131047 Re-engagement fuera de la ventana de 24h) con HTTP 200 + un objeto "error" en el
    /// body. Sin tratarlo, SendTextAsync devolvía un wamid inexistente y el bot creía haber
    /// respondido: el método debe detectar el error, no entregar (retornar null) y registrarlo.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_GraphErrorIn200_NotDelivered()
    {
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "test-token");
        var context = new Mock<ITenantContext>();
        context.Setup(c => c.IsSet).Returns(true);
        context.Setup(c => c.PhoneNumberId).Returns("123456789");
        context.Setup(c => c.WhatsAppAccessToken).Returns("test-token");

        var handler = new GraphErrorHandler();
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            context.Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        // Error 131047 Re-engagement con HTTP 200 (el patrón que dejaba el fallo en silencio).
        var result = await adapter.SendTextAsync("573223697115", "Sí, tienes cita", CancellationToken.None);

        handler.Sent.Should().BeTrue();
        // No entrega: Meta respondió 200 pero rechazó el envío; debe reportarse como no-entregado.
        result.Should().BeNull();
    }

    /// <summary>
    /// Regresión: un template que Meta rechaza (p. ej. no aprobado / fuera de ventana) con 200 + error
    /// no debe entregarse en silencio.
    /// </summary>
    [Fact]
    public async Task SendTemplateAsync_GraphErrorIn200_NotDelivered()
    {
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "test-token");
        var context = new Mock<ITenantContext>();
        context.Setup(c => c.IsSet).Returns(true);
        context.Setup(c => c.PhoneNumberId).Returns("123456789");
        context.Setup(c => c.WhatsAppAccessToken).Returns("test-token");

        var handler = new GraphErrorHandler();
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            context.Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var result = await adapter.SendTemplateAsync(
            "573223697115",
            "recordatorio_24h",
            new Dictionary<string, string> { ["1"] = "Ana" },
            CancellationToken.None);

        handler.Sent.Should().BeTrue();
        result.Should().BeNull();
    }

    /// <summary>
    /// Regresión: una respuesta exitosa (HTTP 200 sin bloque "error", con wamid) sí se entrega.
    /// Garantiza que el nuevo check de efectos colaterales no rompa el happy path.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_Http200_NoGraphError_StillDelivers()
    {
        Environment.SetEnvironmentVariable("WhatsApp__AccessToken", "test-token");
        var context = new Mock<ITenantContext>();
        context.Setup(c => c.IsSet).Returns(true);
        context.Setup(c => c.PhoneNumberId).Returns("123456789");
        context.Setup(c => c.WhatsAppAccessToken).Returns("test-token");

        var handler = new SendCaptureHandler(); // devuelve 200 con wamid válido, sin "error"
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            context.Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var result = await adapter.SendTextAsync("573223697115", "Hola", CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("wamid");
    }

    /// <summary>Construye un payload de webhook típico de Meta con un solo mensaje.</summary>
    private static object BuildWebhookPayload(string from, string contactWaId)
    {
        var message = new Dictionary<string, object?>
        {
            ["from"] = from,
            ["id"] = "wamid.HBkX",
            ["type"] = "text",
            ["text"] = new { body = "Hola" }
        };

        var contacts = contactWaId is null
            ? new object[] { new { profile = new { name = "Cliente" } } }
            : new object[] { new { profile = new { name = "Cliente" }, wa_id = contactWaId } };

        return new
        {
            entry = new object[]
            {
                new
                {
                    changes = new object[]
                    {
                        new
                        {
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new { phone_number_id = "123456789" },
                                contacts,
                                messages = new object[] { message }
                            }
                        }
                    }
                }
            }
        };
    }

    private class GraphErrorHandler : HttpMessageHandler
    {
        public bool Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Sent = true;
            const string errorBody =
                """
                {"error":{"message":"(#3) Re-engagement messages are not possible outside a 24-hour customer service window.","type":"OAuthException","code":131047,"error_subcode":2494010,"fbtrace_id":"abc123"}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(errorBody)
            });
        }
    }

    /// <summary>Responde al nodo de media (JSON snake_case) y al endpoint de descarga (bytes).</summary>
    /// <summary>
    /// Captura el body del POST a Graph /messages y responde un wamid válido (como Meta).
    /// </summary>
    private class SendCaptureHandler : HttpMessageHandler
    {
        public bool Sent { get; private set; }
        public string RequestUrl { get; private set; } = "";
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content != null)
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUrl = request.RequestUri?.ToString() ?? "";
            Sent = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"messages":[{"id":"wamid.HBgAAAA"}]}""")
            };
        }
    }

    private class MediaDownloadHandler : HttpMessageHandler
    {
        public string? LastDownloadUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Contains("graph.facebook.com") == true)
            {
                // Metadatos del media: url en snake_case, tal como lo envía Graph.
                var json = """{"id":"1539873544849939","url":"https://lookaside/media/audio.ogg","mime_type":"audio/ogg","file_size":11}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                });
            }

            // Endpoint de descarga de bytes (lookaside.fbsbx.com).
            LastDownloadUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 })
            });
        }
    }

    private class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new HttpClient(_handler);
    }
}