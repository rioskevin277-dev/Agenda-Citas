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

    /// <summary>Responde al nodo de media (JSON snake_case) y al endpoint de descarga (bytes).</summary>
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