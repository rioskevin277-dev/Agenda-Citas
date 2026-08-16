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