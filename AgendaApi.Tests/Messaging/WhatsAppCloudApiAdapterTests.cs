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

    /// <summary>
    /// Regresión enrutamiento: cuando Meta identifica al remitente con un id de negocio
    /// "from_user_id" (p. ej. "CO.1053765850856674"), ese valor NO es entregable como
    /// destinatario (HTTP 400 #131009). El adaptador debe responder al wa_id E.164 real de
    /// contacts[0], para que el cliente reciba la respuesta del asistente.
    /// </summary>
    [Fact]
    public async Task ParseWebhookPayload_BusinessIdFrom_RespondsToContactWaId()
    {
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(new MediaDownloadHandler()),
            new Mock<ITenantContext>().Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var body = BuildWebhookPayload(
            from: "CO.1053765850856674",       // id de negocio, no entregable
            contactWaId: "573211122233",        // el número real del payload
            fromUserId: "CO.1053765850856674",
            fromUserIdPresent: true);

        var msgs = await adapter.ParseWebhookPayloadAsync(body);

        msgs.Should().HaveCount(1);
        // El destinatario usado para la respuesta debe ser el wa_id E.164 real, NO el id CO.x.
        msgs[0].From.Should().Be("573211122233");
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
    public async Task ParseWebhookPayload_OnlyBusinessId_KeepsItAsIdentity()
    {
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(new MediaDownloadHandler()),
            new Mock<ITenantContext>().Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        // Sin from E.164 válido ni wa_id: solo existe el id de negocio → se conserva como
        // identidad (sin un número real no hay destinatario alternativo).
        var body = new
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
                                // contacts NO lleva wa_id entregable
                                contacts = new object[] { new { profile = new { name = "X" } } },
                                messages = new object[]
                                {
                                    new
                                    {
                                        from = "CO.1053765850856674",
                                        from_user_id = "CO.1053765850856674",
                                        id = "wamid.HBk",
                                        type = "text",
                                        text = new { body = "Hola" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var msgs = await adapter.ParseWebhookPayloadAsync(body);

        msgs.Should().HaveCount(1);
        msgs[0].From.Should().Be("CO.1053765850856674");
    }

    /// <summary>
    /// Regresión (vía "Instagram"): un contacto que llega únicamente como id virtualizado "CO.x"/
    /// user_id — mensaje vía Instagram en el inbox unificado de Meta (sin teléfono). El endpoint
    /// de WhatsApp lo rechaza como "número de teléfono incorrecto" (#131009), así que el envío
    /// debe ir por la Instagram Messaging API: POST /{cuenta-IG-business}/messages usando la cuenta
    /// IG business como emisora (path) y el id numérico REAL del destinatario (sin el prefijo "CO.")
    /// como recipient.id, con message.text + messaging_type "RESPONSE" y el token IG.
    /// </summary>
    [Fact]
    public async Task SendTextAsync_NonE164Recipient_SendsViaInstagramApi()
    {
        Environment.SetEnvironmentVariable("Instagram__AccessToken", "test-ig-token");
        Environment.SetEnvironmentVariable("Instagram__BusinessAccountId", "17841476963658642");
        var handler = new SendCaptureHandler();
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            new Mock<ITenantContext>().Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var result = await adapter.SendTextAsync("CO.1053765850856674", "Hola", CancellationToken.None);

        handler.Sent.Should().BeTrue();
        // Endpoint de la Instagram Messaging API: /{cuenta-IG-business}/messages (emisora), NO el CO.x.
        handler.RequestUrl.Should().Contain("/17841476963658642/messages");
        result.Should().BeNull(); // el fake no devuelve "message_id", solo campo ausente
        // Body con el contrato de IG: recipient (id numérico sin prefijo CO.), message y RESPONSE.
        handler.RequestBody.Should().Contain("\"recipient\":{\"id\":\"1053765850856674\"}");
        handler.RequestBody.Should().Contain("\"message\":{\"text\":\"Hola\"}");
        handler.RequestBody.Should().Contain("\"messaging_type\":\"RESPONSE\"");
    }

    /// <summary>
    /// Un número E.164 (WhatsApp normal) se envía con source_type explícito WHATSAPP y el número
    /// como `to`: no se altera el routing del caso estándar.
    /// </summary>
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
    /// Regresión "Instagram code 3": sin la capacidad de mensajería de IG (App Review pendiente),
    /// Meta responde 200 + error code 3. El DM no se entrega (retorna null) en lugar de "enviado".
    /// </summary>
    [Fact]
    public async Task SendInstagramDirect_Code3Capability_NotDelivered()
    {
        Environment.SetEnvironmentVariable("Instagram__AccessToken", "test-ig-token");
        Environment.SetEnvironmentVariable("Instagram__BusinessAccountId", "17841476963658642");
        var handler = new GraphErrorHandler();
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            new Mock<ITenantContext>().Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var result = await adapter.SendTextAsync("CO.1053765850856674", "Hola", CancellationToken.None);

        handler.Sent.Should().BeTrue();
        result.Should().BeNull(); // no entregado: app sin capacidad de mensajería de IG
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
    private static object BuildWebhookPayload(string from, string contactWaId, string? fromUserId = null, bool fromUserIdPresent = false)
    {
        var message = new Dictionary<string, object?>
        {
            ["from"] = from,
            ["id"] = "wamid.HBkX",
            ["type"] = "text",
            ["text"] = new { body = "Hola" }
        };
        if (fromUserIdPresent)
            message["from_user_id"] = fromUserId;

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

    /// <summary>
    /// Regresión fail-safe: la ruta Instagram (destinatarios NO-telefónicos, id virtualizado XX.)
    /// no debe lanzar excepción cuando Meta responde HTTP != 200; debe devolver null, de lo
    /// contrario un solo DM de IG tumba el flush del cliente ("[ERR] Error en flush").
    /// </summary>
    [Fact]
    public async Task SendInstagramDirect_HttpErrorStatus_DoesNotThrow_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("Instagram__AccessToken", "test-ig-token");
        Environment.SetEnvironmentVariable("Instagram__BusinessAccountId", "17841476963658642");
        var handler = new ServerErrorHandler(); // responde HTTP 500
        var adapter = new WhatsAppCloudApiAdapter(
            new FakeHttpClientFactory(handler),
            new Mock<ITenantContext>().Object,
            NullLogger<WhatsAppCloudApiAdapter>.Instance);

        var act = () => adapter.SendTextAsync("PE.1093281683152939", "Hola", CancellationToken.None);

        // No debe lanzar: devuelve null (no entregado) para no romper el turno.
        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeNull();
    }

    /// <summary>Devuelve HTTP 200 con un objeto "error" de Graph (lo que Meta hace ante fallos de
    /// entrega reales como 131047/code 3) para verificar que el envío no se "finge" entregado.</summary>
    private class ServerErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"message\":\"server error\"}}")
            });
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