SET NOCOUNT ON;
SELECT 'AUDIO-CONV', role, LEFT(content,150) AS content, fecha_creacion
FROM conversation_messages
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND phone_cliente LIKE '%573216403049'
ORDER BY fecha_creacion DESC;
SELECT 'CITA', id_appointment, fecha_inicio, fecha_fin, estado, external_event_id
FROM appointments
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB' AND id_appointment='7C37B6DD-BA1F-4124-AA57-367CDBB9809D';