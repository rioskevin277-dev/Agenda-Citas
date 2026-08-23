SET NOCOUNT ON;
SELECT TOP 3 'CITA', id_appointment, fecha_inicio, fecha_fin, estado, id_service_type, id_professional, external_event_id, confirmado_en
FROM appointments
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
ORDER BY fecha_creacion DESC;
SELECT TOP 3 'CONV', role, LEFT(content,120) AS content, fecha_creacion
FROM conversation_messages
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND phone_cliente LIKE '%573216403049'
ORDER BY fecha_creacion DESC;
SELECT 'CLIENTE', whatsapp, estado, proxima_cita FROM clients
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB' AND whatsapp LIKE '%573216403049';