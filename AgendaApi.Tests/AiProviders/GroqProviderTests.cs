using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgendaApi.Domain.Ports;
using AgendaApi.Infrastructure.AiProviders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgendaApi.Tests.AiProviders;

/// <summary>
/// El tier gratuito de Groq (on_demand) limita tokens/minuto. El 429 que devuelve cuando se
/// agota la cuota es transitorio ("Please try again in Xs"), así que GroqProvider debe
/// reintentar con backoff en vez de rendirse al primer intento. Estos tests garantizan que
/// ante 429 se reintenta y se completa, mientras que errores definitivos (500) no reintentán.
/// </summary>
[Collection("GroqEnv")]
public class GroqProviderTests
{
    private const string ApiKey = "gsk_test_api_key";
    private const string ChatUrl = "https://api.groq.com/openai/v1/chat/completions";

    private static readonly List<ChatMessage> Msgs = new()
    {
        new ChatMessage { Role = "user", Content = "Hola" }
    };

    /// <summary>429 en el primer intento y 200 en el segundo → debe reintentar (2 llamadas) y contestar.</summary>
    [Fact]
    public async Task GenerateResponseAsync_RateLimitedThenSucceeds_RetriesAndReturns()
    {
        Environment.SetEnvironmentVariable("Groq__ApiKey", ApiKey);
        var handler = new StubHandler(
            (429, """{"error":{"code":"rate_limit_exceeded","message":"Please try again in 0.1s"}}"""),
            (200, """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}"""));

        var provider = new GroqProvider(new StubFactory(handler), NullLogger<GroqProvider>.Instance);

        var text = await provider.GenerateResponseAsync(Msgs);

        text.Should().Be("ok");
        handler.SentRequests.Should().Be(2);
        handler.LastUrl.Should().Be(ChatUrl);
    }

    /// <summary>429 tres veces seguidas → se agotan los reintentos y debe lanzar (no escalar como éxito).</summary>
    [Fact]
    public async Task GenerateResponseAsync_PersistentRateLimit_ThrowsAfterRetries()
    {
        Environment.SetEnvironmentVariable("Groq__ApiKey", ApiKey);
        var handler = new StubHandler(
            (429, """{"error":{"message":"Please try again in 0.1s"}}"""),
            (429, """{"error":{"message":"Please try again in 0.1s"}}"""),
            (429, """{"error":{"message":"Please try again in 0.1s"}}"""));

        var provider = new GroqProvider(new StubFactory(handler), NullLogger<GroqProvider>.Instance);

        var act = async () => await provider.GenerateResponseAsync(Msgs);

        await act.Should().ThrowAsync<Exception>();
        // 3 intentos máximos, no más.
        handler.SentRequests.Should().Be(3);
    }

    /// <summary>Error 500 (definitivo) → NO debe reintentar; falla al primer intento.</summary>
    [Fact]
    public async Task GenerateResponseAsync_NonRetryableError_DoesNotRetry()
    {
        Environment.SetEnvironmentVariable("Groq__ApiKey", ApiKey);
        var handler = new StubHandler((500, "boom"));

        var provider = new GroqProvider(new StubFactory(handler), NullLogger<GroqProvider>.Instance);

        var act = async () => await provider.GenerateResponseAsync(Msgs);

        await act.Should().ThrowAsync<Exception>();
        handler.SentRequests.Should().Be(1);
    }

    /// <summary>Tool calling: 429 → 200 con tool_calls; debe reintentar y devolver Success con la tool.</summary>
    [Fact]
    public async Task GenerateResponseWithToolsAsync_RateLimitedThenSucceeds_ReturnsToolCall()
    {
        Environment.SetEnvironmentVariable("Groq__ApiKey", ApiKey);
        var toolJson = """
            {"model":"x","choices":[{"message":{"role":"assistant","content":null,
            "tool_calls":[{"id":"call_1","type":"function","function":{"name":"check_availability","arguments":"{}"}}]},
            "finish_reason":"tool_calls"}]}
            """;
        var handler = new StubHandler(
            (429, """{"error":{"message":"Please try again in 0.1s"}}"""),
            (200, toolJson));

        var provider = new GroqProvider(new StubFactory(handler), NullLogger<GroqProvider>.Instance);

        var result = await provider.GenerateResponseWithToolsAsync(Msgs, new List<object>());

        result.Success.Should().BeTrue();
        result.FinishReason.Should().Be("tool_calls");
        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls![0].Name.Should().Be("check_availability");
        handler.SentRequests.Should().Be(2);
    }

    /// <summary>400 con "tool_use_failed" (falla de generación del modelo) → debe reintentar y completar.</summary>
    [Fact]
    public async Task GenerateResponseWithToolsAsync_ToolUseFailedThenSucceeds_Retries()
    {
        Environment.SetEnvironmentVariable("Groq__ApiKey", ApiKey);
        var toolJson = """
            {"model":"x","choices":[{"message":{"role":"assistant","content":null},
            "finish_reason":"stop"}]}
            """;
        var badJson = """{"error":{"message":"Failed to call a function.","type":"invalid_request_error","code":"tool_use_failed"}}""";
        var handler = new StubHandler(
            (400, badJson),
            (200, toolJson));

        var provider = new GroqProvider(new StubFactory(handler), NullLogger<GroqProvider>.Instance);

        var result = await provider.GenerateResponseWithToolsAsync(Msgs, new List<object>());

        result.Success.Should().BeTrue();
        handler.SentRequests.Should().Be(2);
    }

    /// <summary>400 genérico (sin "tool_use_failed") → NO debe reintentar (falla definitiva).</summary>
    [Fact]
    public async Task GenericBadRequest_DoesNotRetry()
    {
        Environment.SetEnvironmentVariable("Groq__ApiKey", ApiKey);
        var handler = new StubHandler((400, """{"error":{"message":"bad request"}}"""));

        var provider = new GroqProvider(new StubFactory(handler), NullLogger<GroqProvider>.Instance);

        var result = await provider.GenerateResponseWithToolsAsync(Msgs, new List<object>());

        result.Success.Should().BeFalse();
        handler.SentRequests.Should().Be(1);
    }

    // ─── Handler y factory de prueba ─────────────────────────────────────

    /// <summary>Devuelve una secuencia de respuestas (status, body) y cuenta los envíos.</summary>
    private class StubHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _responses;
        private int _index;

        public int SentRequests { get; private set; }
        public string? LastUrl { get; private set; }

        public StubHandler(params (int Status, string Body)[] responses)
        {
            _responses = responses
                .Select(r => ((HttpStatusCode)r.Status, r.Body))
                .ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SentRequests++;
            LastUrl = request.RequestUri?.ToString();
            var (status, body) = _responses[Math.Min(_index++, _responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new HttpClient(_handler);
    }
}