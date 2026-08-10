DECLARE @tenant UNIQUEIDENTIFIER = '87CAB46A-C019-413F-8309-2A9D515F89AB';

IF NOT EXISTS (SELECT 1 FROM service_types WHERE id_tenant = @tenant)
BEGIN
  INSERT INTO service_types (id_service_type, id_tenant, nombre, descripcion, duracion_minutos, buffer_minutos, precio, activo, fecha_creacion) VALUES
   (NEWID(), @tenant, 'Consulta General', 'Consulta medica general', 30, 5, 50000, 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 'Consulta de Control', 'Seguimiento de tratamiento', 20, 5, 30000, 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 'Procedimiento Menor', 'Procedimientos ambulatorios', 45, 10, 80000, 1, SYSUTCDATETIME());
END

IF NOT EXISTS (SELECT 1 FROM availability_rules WHERE id_tenant = @tenant)
BEGIN
  INSERT INTO availability_rules (id_availability_rule, id_tenant, dia_semana, hora_inicio, hora_fin, activo, fecha_creacion) VALUES
   (NEWID(), @tenant, 1, '09:00', '18:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 2, '09:00', '18:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 3, '09:00', '18:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 4, '09:00', '18:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 5, '09:00', '18:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, 6, '09:00', '13:00', 1, SYSUTCDATETIME());
END

-- ── Profesionales (Fase 1) ────────────────────────────────────────
DECLARE @maria UNIQUEIDENTIFIER;
IF EXISTS (SELECT 1 FROM professionals WHERE id_tenant = @tenant AND nombre = 'Dra. María')
  SELECT @maria = id_professional FROM professionals WHERE id_tenant = @tenant AND nombre = 'Dra. María';
ELSE
BEGIN
  SET @maria = NEWID();
  INSERT INTO professionals (id_professional, id_tenant, nombre, email, telefono, activo, fecha_creacion) VALUES
   (@maria, @tenant, 'Dra. María', 'maria@clinica.test', '+573001112233', 1, SYSUTCDATETIME());
END

DECLARE @carlos UNIQUEIDENTIFIER;
IF EXISTS (SELECT 1 FROM professionals WHERE id_tenant = @tenant AND nombre = 'Dr. Carlos')
  SELECT @carlos = id_professional FROM professionals WHERE id_tenant = @tenant AND nombre = 'Dr. Carlos';
ELSE
BEGIN
  SET @carlos = NEWID();
  INSERT INTO professionals (id_professional, id_tenant, nombre, email, telefono, activo, fecha_creacion) VALUES
   (@carlos, @tenant, 'Dr. Carlos', 'carlos@clinica.test', '+573001122334', 1, SYSUTCDATETIME());
END

-- Cartera: ambos realizan los 3 servicios del tenant
INSERT INTO professional_services (id_professional, id_service_type, activo)
SELECT @maria, s.id_service_type, 1 FROM service_types s WHERE s.id_tenant = @tenant
  AND NOT EXISTS (SELECT 1 FROM professional_services ps WHERE ps.id_professional = @maria AND ps.id_service_type = s.id_service_type);
INSERT INTO professional_services (id_professional, id_service_type, activo)
SELECT @carlos, s.id_service_type, 1 FROM service_types s WHERE s.id_tenant = @tenant
  AND NOT EXISTS (SELECT 1 FROM professional_services ps WHERE ps.id_professional = @carlos AND ps.id_service_type = s.id_service_type);

-- Horario personal de Dra. María (lun-vie 09-17) — demuestra el override "profesional sobre negocio"
IF NOT EXISTS (SELECT 1 FROM availability_rules WHERE id_tenant = @tenant AND id_professional = @maria)
BEGIN
  INSERT INTO availability_rules (id_availability_rule, id_tenant, id_professional, dia_semana, hora_inicio, hora_fin, activo, fecha_creacion) VALUES
   (NEWID(), @tenant, @maria, 1, '09:00', '17:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, @maria, 2, '09:00', '17:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, @maria, 3, '09:00', '17:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, @maria, 4, '09:00', '17:00', 1, SYSUTCDATETIME()),
   (NEWID(), @tenant, @maria, 5, '09:00', '17:00', 1, SYSUTCDATETIME());
END

SELECT 'SERVICIOS' AS Tipo, nombre, duracion_minutos, buffer_minutos, precio, activo FROM service_types WHERE id_tenant = @tenant;
SELECT 'HORARIOS' AS Tipo, dia_semana, hora_inicio, hora_fin, activo FROM availability_rules WHERE id_tenant = @tenant ORDER BY dia_semana;
SELECT 'PROFESIONALES' AS Tipo, id_professional, nombre, email, telefono, activo FROM professionals WHERE id_tenant = @tenant;
SELECT ps.id_professional, p.nombre AS profesional, st.nombre AS servicio
  FROM professional_services ps
  JOIN professionals p ON p.id_professional = ps.id_professional
  JOIN service_types st ON st.id_service_type = ps.id_service_type
  WHERE p.id_tenant = @tenant;
