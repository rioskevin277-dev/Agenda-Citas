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
/// OpenRouter es el respaldo gratuito de Groq (gateway OpenAI-compatible con modelos :free).
/// Mismas garantías que GroqProvider: ante 429/errores transitorios no responde texto de éxito,
/// parsea tool calls correctamente, y sin API key no cae en un HTTP 401 (falla rápido).
/// Comparte la colección "GroqEnv" porque muta variables de entorno globales.
/// </summary>
[Collection("GroqEnv")]
public class OpenRouterProviderTests
{
    private const string ApiKey = "sk-or-test-key";
    private static readonly List<ChatMessage> Msgs = new()
    {
        new ChatMessage { Role = "user", Content = "Hola" }
    };

    private static OpenRouterProvider CreateProvider(StubHandler handler)
        => new(new StubFactory(handler), NullLogger<OpenRouterProvider>.Instance);

    [Fact]
    public async Task GenerateResponseAsync_WithKey_ReturnsText()
    {
        Environment.SetEnvironmentVariable("OpenRouter__ApiKey", ApiKey);
        var handler = new StubHandler(
            (200, """{"choices":[{"message":{"role":"assistant","content":"hola"}}]}"""));
        var provider = CreateProvider(handler);

        var text = await provider.GenerateResponseAsync(Msgs);

        text.Should().Be("hola");
        handler.LastUrl.Should().Be("https://openrouter.ai/api/v1/chat/completions");
        handler.LastAuthorization.Should().Be($"Bearer {ApiKey}");
    }

    [Fact]
    public async Task GenerateResponseWithToolsAsync_ToolCall_ReturnsTool()
    {
        Environment.SetEnvironmentVariable("OpenRouter__ApiKey", ApiKey);
        var toolJson = """
            {"choices":[{"message":{"role":"assistant","content":null,
            "tool_calls":[{"id":"call_1","type":"function","function":{"name":"check_availability","arguments":"{}"}}]},
            "finish_reason":"tool_calls"}]}
            """;
        var provider = CreateProvider(new StubHandler((200, toolJson)));

        var result = await provider.GenerateResponseWithToolsAsync(Msgs, new List<object>());

        result.Success.Should().BeTrue();
        result.FinishReason.Should().Be("tool_calls");
        result.ToolCalls![0].Name.Should().Be("check_availability");
    }

    [Fact]
    public async Task GenerateResponseWithToolsAsync_ApiError_ReturnsFailureNotThrow()
    {
        Environment.SetEnvironmentVariable("OpenRouter__ApiKey", ApiKey);
        var provider = CreateProvider(new StubHandler((429, """{"error":"limit"}""")));

        var result = await provider.GenerateResponseWithToolsAsync(Msgs, new List<object>());

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateResponseAsync_NoApiKey_Throws()
    {
        Environment.SetEnvironmentVariable("OpenRouter__ApiKey", null);
        var provider = CreateProvider(new StubHandler((200, """{"choices":[]}""")));

        var act = async () => await provider.GenerateResponseAsync(Msgs);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateResponseAsync_PlaceholderKey_Throws()
    {
        Environment.SetEnvironmentVariable("OpenRouter__ApiKey", "sk-or-xxx-test");
        var provider = CreateProvider(new StubHandler((200, """{"choices":[]}""")));

        var act = async () => await provider.GenerateResponseAsync(Msgs);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── Handler y factory de prueba ─────────────────────────────────────

    private class StubHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _responses;
        private int _index;

        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }

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
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            var (status, body) = _responses[Math.Min(_index++, _responses.Length - 1)];
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("HTTP-Referer", "https://agenda-api.local");
            return Task.FromResult(response);
        }
    }

    private class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new HttpClient(_handler);
    }
}