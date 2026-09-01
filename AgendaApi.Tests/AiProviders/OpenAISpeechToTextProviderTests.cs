using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgendaApi.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgendaApi.Tests.AiProviders;

[Collection("GroqEnv")]
public class OpenAISpeechToTextProviderTests
{
    private const string ApiKey = "sk-test_openai_key";

    private static OpenAISpeechToTextProvider CreateProvider(FakeHttpHandler handler)
    {
        var factory = new FakeHttpClientFactory(handler);
        return new OpenAISpeechToTextProvider(factory, NullLogger<OpenAISpeechToTextProvider>.Instance);
    }

    [Fact]
    public async Task TranscribeAsync_WithValidAudio_ReturnsText()
    {
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", ApiKey);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, """{"text":"Hola, quiero agendar una cita para mañana"}""");
        var provider = CreateProvider(handler);

        var result = await provider.TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/ogg");

        result.Should().Be("Hola, quiero agendar una cita para mañana");
        handler.LastUrl.Should().Be("https://api.openai.com/v1/audio/transcriptions");
        handler.LastAuthorization.Should().Be($"Bearer {ApiKey}");
    }

    [Fact]
    public async Task TranscribeAsync_ApiError_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", ApiKey);
        var provider = CreateProvider(new FakeHttpHandler(HttpStatusCode.InternalServerError, "error"));

        var result = await provider.TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/ogg");

        result.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeAsync_EmptyText_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", ApiKey);
        var provider = CreateProvider(new FakeHttpHandler(HttpStatusCode.OK, """{"text":"   "}"""));

        var result = await provider.TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/ogg");

        result.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeAsync_NullAudio_ReturnsNull_WithoutCallingApi()
    {
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", ApiKey);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, """{"text":"x"}""");
        var provider = CreateProvider(handler);

        var result = await provider.TranscribeAsync(System.Array.Empty<byte>(), "audio/ogg");

        result.Should().BeNull();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task TranscribeAsync_MimeWithCodecsParam_ReturnsText()
    {
        // Graph devuelve el MIME con parámetros ("audio/ogg; codecs=opus"); antes esto
        // rompía en MediaTypeHeaderValue (FormatException). Debe tolerarlo.
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", ApiKey);
        var handler = new FakeHttpHandler(HttpStatusCode.OK, """{"text":"Quiero una cita el lunes"}""");
        var provider = CreateProvider(handler);

        var result = await provider.TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/ogg; codecs=opus");

        result.Should().Be("Quiero una cita el lunes");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TranscribeAsync_NoApiKey_Throws()
    {
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", null);
        var provider = CreateProvider(new FakeHttpHandler(HttpStatusCode.OK, """{"text":"x"}"""));

        var act = async () => await provider.TranscribeAsync(new byte[] { 1 }, "audio/ogg");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TranscribeAsync_PlaceholderApiKey_Throws()
    {
        Environment.SetEnvironmentVariable("OpenAI__ApiKey", "sk-xxx-placeholder");
        var provider = CreateProvider(new FakeHttpHandler(HttpStatusCode.OK, """{"text":"x"}"""));

        var act = async () => await provider.TranscribeAsync(new byte[] { 1 }, "audio/ogg");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── HttpMessageHandler falso + factory ─────────────────────────────

    private class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }
        public int CallCount { get; private set; }

        public FakeHttpHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return await Task.FromResult(response);
        }
    }

    private class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name)
            => new HttpClient(_handler);
    }
}