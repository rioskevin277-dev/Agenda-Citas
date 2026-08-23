SET NOCOUNT ON;
SELECT TOP 4 role, LEFT(content,200) AS content, fecha_creacion
FROM conversation_messages
WHERE id_tenant='87CAB46A-C019-413F-8309-2A9D515F89AB'
  AND phone_cliente LIKE '%573216403049'
ORDER BY fecha_creacion DESC;