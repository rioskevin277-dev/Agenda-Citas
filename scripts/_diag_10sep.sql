USE AgendaApi;
SET NOCOUNT ON;

-- Columnas de las tablas relevantes
SELECT c.TABLE_NAME AS tabla, c.COLUMN_NAME AS col
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_NAME IN ('service_types','availability_rules','availability_exceptions','appointments','professionals','professional_services','tenants','clients')
ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION;

PRINT '=== SERVICE TYPES / TENANT 5acd2cd0 ===';
SELECT Id, Name, DuracionMinutos, CapacidadMaxima
FROM service_types
WHERE Name LIKE '%Corte%';

PRINT '=== AVAILABILITY RULES 09-SEP al 11-SEP (diapivote) ===';
SELECT Id, IdTenant, IdProfessional, DiaSemana, HoraInicio, HoraFin, Activo
FROM availability_rules
ORDER BY IdProfessional, DiaSemana;

PRINT '=== EXCEPTIONS 10-SEP ===';
SELECT Id, IdTenant, IdProfessional, Fecha, TodoElDia, HoraInicio, HoraFin, Nombre
FROM availability_exceptions
WHERE Fecha >= '2026-09-10' AND Fecha < '2026-09-11';

PRINT '=== CITAS 10-SEP (cualquier estado) ===';
SELECT Id, IdTenant, IdServiceType, IdProfessional, FechaInicio, FechaFin, Estado
FROM appointments
WHERE FechaInicio >= '2026-09-10' AND FechaInicio < '2026-09-11'
ORDER BY FechaInicio;