using AgendaApi.Infrastructure.Services;
using FluentAssertions;

namespace AgendaApi.Tests.Services;

public class ToolActionSummarizerTests
{
    [Fact]
    public void CheckAvailability_Success_ReportsSlotCount()
    {
        const string json = """
            {"success":true,"slots":[{"start":"2026-08-12T10:00:00Z"},{"start":"2026-08-12T11:00:00Z"}],"total_slots":2}
            """;

        ToolActionSummarizer.Summarize("check_availability", json)
            .Should().Be("Consultó disponibilidad: 2 horario(s) ofrecido(s).");
    }

    [Fact]
    public void CreateAppointment_Success_ReportsDetail()
    {
        const string json = """
            {"success":true,"appointment":{"id":"aaaaaaaa","serviceType":"Consulta General","professional":"Dra. Maria","start":"2026-08-12T15:00:00Z","end":"2026-08-12T15:30:00Z","status":"pending"}}
            """;

        var result = ToolActionSummarizer.Summarize("create_appointment", json);

        result.Should().Contain("Agendó una cita");
        result.Should().Contain("Consulta General");
        result.Should().Contain("Dra. Maria");
        result.Should().Contain("estado pending");
    }

    [Fact]
    public void CancelAppointment_Success_ShortForm()
    {
        const string json = """{"success":true,"appointment":{"id":"12345678","status":"cancelled"}}""";

        ToolActionSummarizer.Summarize("cancel_appointment", json)
            .Should().Be("Canceló la cita 12345678.");
    }

    [Fact]
    public void RescheduleAppointment_Success_ReportsNewStart()
    {
        const string json = """{"success":true,"appointment":{"id":"12345678","newStart":"2026-08-14T14:00:00Z","status":"pending"}}""";

        ToolActionSummarizer.Summarize("reschedule_appointment", json)
            .Should().StartWith("Reprogramó una cita a ")
            .And.EndWith(".");
    }

    [Fact]
    public void ListAppointments_Success_ReportsTotal()
    {
        const string json = """{"success":true,"total":3,"appointments":[]}""";

        ToolActionSummarizer.Summarize("list_appointments", json)
            .Should().Be("Consultó las citas del cliente: 3 encontrada(s).");
    }

    [Fact]
    public void ToolWithError_ReportsFailureWithReason()
    {
        const string json = """{"success":false,"error":"El horario ya esta ocupado."}""";

        ToolActionSummarizer.Summarize("create_appointment", json)
            .Should().Be("Fallo al agendar la cita: El horario ya esta ocupado.");
    }

    [Fact]
    public void UnknownTool_ReportsGenericAction()
    {
        ToolActionSummarizer.Summarize("some_tool", "{}").Should().Be("Ejecutó la herramienta some_tool.");
    }

    [Fact]
    public void MalformedJson_DoesNotThrow()
    {
        ToolActionSummarizer.Summarize("create_appointment", "not-json").Should().Be("Ejecutó la herramienta create_appointment.");
    }
}