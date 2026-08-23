SET NOCOUNT ON;
SELECT 'CONV', role, LEFT(content,150) AS content, fecha_creacion
FROM conversation_messages
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND phone_cliente LIKE '%573216403049'
  AND fecha_creacion > '2026-08-16 19:05:00'
ORDER BY fecha_creacion ASC;
SELECT 'CITA', a.id_appointment, a.fecha_inicio, a.fecha_fin, a.estado, a.external_event_id,
       st.nombre AS servicio, p.nombre AS profesional
FROM appointments a
LEFT JOIN service_types st ON st.id_service_type=a.id_service_type
LEFT JOIN professionals p ON p.id_professional=a.id_professional
WHERE a.id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND a.fecha_inicio > '2026-08-16'
ORDER BY a.fecha_inicio DESC;