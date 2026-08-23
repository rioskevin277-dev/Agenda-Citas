SET NOCOUNT ON;
SELECT c.whatsapp, a.id_appointment, a.estado, st.nombre AS servicio,
       CONVERT(varchar, a.fecha_inicio, 120) AS fecha_inicio, pr.nombre AS profesional
FROM appointments a
JOIN clients c ON c.id_client = a.id_client
LEFT JOIN service_types st ON st.id_service_type = a.id_service_type
LEFT JOIN professionals pr ON pr.id_professional = a.id_professional
WHERE c.whatsapp = '573216403049'
ORDER BY a.fecha_inicio;