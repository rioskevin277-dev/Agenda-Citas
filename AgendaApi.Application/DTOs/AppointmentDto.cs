namespace AgendaApi.Application.DTOs;

public record AppointmentCreateDto
{
    public Guid TenantId { get; init; }
    public Guid? ClientId { get; init; }
    public Guid? ServiceTypeId { get; init; }

    /// <summary>Lookup alternativo por WhatsApp (cuando no se conoce ClientId)</summary>
    public string? ClientWhatsApp { get; init; }

    /// <summary>Lookup alternativo por nombre (cuando no se conoce ServiceTypeId)</summary>
    public string? ClientName { get; init; }

    /// <summary>Lookup alternativo por nombre de servicio</summary>
    public string? ServiceTypeName { get; init; }

    public DateTime FechaInicio { get; init; }
    public DateTime FechaFin { get; init; }
    public string? Notas { get; init; }
}

public record AppointmentRescheduleDto
{
    public Guid AppointmentId { get; init; }

    /// <summary>Lookup alternativo: WhatsApp del cliente (cuando no se conoce el ID real de la cita).</summary>
    public string? AppointmentIdentifier { get; init; }

    public Guid TenantId { get; init; }
    public DateTime NuevaFechaInicio { get; init; }
    public DateTime NuevaFechaFin { get; init; }
}

public record AppointmentCancelDto
{
    public Guid? AppointmentId { get; init; }

    /// <summary>Lookup alternativo: WhatsApp del cliente o descripción de la cita</summary>
    public string? AppointmentIdentifier { get; init; }
    public Guid TenantId { get; init; }
    public string? Motivo { get; init; }
}

public record AppointmentResponseDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid ClientId { get; init; }
    public string? ClientName { get; init; }
    public Guid ServiceTypeId { get; init; }
    public string? ServiceTypeName { get; init; }
    public DateTime FechaInicio { get; init; }
    public DateTime FechaFin { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ExternalEventId { get; init; }
    public string? Notas { get; init; }
}

public record AvailabilityQueryDto
{
    public Guid TenantId { get; init; }
    public DateOnly FechaInicio { get; init; }
    public DateOnly FechaFin { get; init; }
    public Guid? ServiceTypeId { get; init; }

    /// <summary>Lookup alternativo por nombre</summary>
    public string? ServiceTypeName { get; init; }
}

public record TimeSlotDto
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool Disponible { get; init; }
    public string? ServiceTypeName { get; init; }
}

public record ClientDto
{
    public Guid Id { get; init; }
    public string WhatsApp { get; init; } = string.Empty;
    public string? Nombre { get; init; }
    public string? Email { get; init; }
}

public record ServiceTypeDto
{
    public Guid Id { get; init; }
    public string Nombre { get; init; } = string.Empty;
    public string? Descripcion { get; init; }
    public int DuracionMinutos { get; init; }
    public int BufferMinutos { get; init; }
    public decimal? Precio { get; init; }
}
