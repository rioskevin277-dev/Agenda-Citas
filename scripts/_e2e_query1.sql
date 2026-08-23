SET NOCOUNT ON;
SELECT 'CLIENTE' AS seccion, whatsapp, nombre, estado, tags, ultima_interaccion
FROM clients
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND whatsapp LIKE '%573216403049';
SELECT 'CONV' AS seccion, role, LEFT(content,90) AS content, fecha_creacion
FROM conversation_messages
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
ORDER BY fecha_creacion DESC;