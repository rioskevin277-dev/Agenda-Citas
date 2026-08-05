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

SELECT 'SERVICIOS' AS Tipo, nombre, duracion_minutos, buffer_minutos, precio, activo FROM service_types WHERE id_tenant = @tenant;
SELECT 'HORARIOS' AS Tipo, dia_semana, hora_inicio, hora_fin, activo FROM availability_rules WHERE id_tenant = @tenant ORDER BY dia_semana;
