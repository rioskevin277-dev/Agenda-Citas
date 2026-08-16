using Xunit;

namespace AgendaApi.Tests.AiProviders;

/// <summary>
/// Colección compartida para clases que mutan la misma variable de entorno global
/// (Groq__ApiKey). Sin esta colección, xUnit corre las clases en paralelo y un test que
/// setea el env a null (NoApiKey_Throws) pisaría el valor que otro test paralelo está leyendo.
/// Al agruparlas en la misma colección, se ejecutan de forma serializada.
/// </summary>
[CollectionDefinition("GroqEnv", DisableParallelization = true)]
public class GroqEnvCollection
{
}