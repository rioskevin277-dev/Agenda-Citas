namespace AgendaApi.Domain.Ports;

/// <summary>
/// Informa si un cliente tiene una sesión de WhatsApp activa (ventana de 24h).
/// Se usa para decidir si un mensaje de texto libre puede enviarse (solo en ventana)
/// o si se requiere un template aprobado (fuera de ventana, error 131047).
/// </summary>
public interface IConversationSessionService
{
    bool HasActiveSession(Guid tenantId, string userPhone);
}
