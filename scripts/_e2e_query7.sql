SET NOCOUNT ON;
SELECT 'WAITLIST', id_waitlist_entry, id_client, id_service_type, id_professional, fecha_desde, fecha_hasta, estado, fecha_creacion
FROM waitlist_entries
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
ORDER BY fecha_creacion DESC;
SELECT 'CONV', role, LEFT(content,170) AS content, fecha_creacion
FROM conversation_messages
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND phone_cliente LIKE '%573216403049'
  AND fecha_creacion > '2026-08-16 19:12:00'
ORDER BY fecha_creacion ASC;