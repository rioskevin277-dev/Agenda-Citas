-- ============================================================================
-- RECONVERSIÓN DEL TENANT REAL COMO CONSULTORIO ODONTOLÓGICO INVENTADO
-- Tenant: 87CAB46A-C019-413F-8309-2A9D515F89AB  ("Clinica Test" -> "Clínica Dental Sonrisa")
-- ----------------------------------------------------------------------------
-- OBJETIVO: moldear el único tenant real (único número WhatsApp 1176778925528955)
-- como el negocio inventado para el E2E completo, SIN romper los appointments
-- (7) ni clients (4) existentes. Se conservan TODOS los IDs de service_types y
-- professionals para que las referencias históricas sigan apuntando a filas válidas.
-- ============================================================================

DECLARE @tenant UNIQUEIDENTIFIER = '87CAB46A-C019-413F-8309-2A9D515F89AB';

-- ── 1) IDENTIDAD DEL NEGOCIO (marca + contacto + reglas de agendamiento) ──
UPDATE tenants SET
  nombre                = 'Clínica Dental Sonrisa',
  nombre_comercial      = 'Clínica Dental Sonrisa',
  correo                = 'contacto@sonrisa.dental',
  telefono              = '+573001234567',
  direccion             = 'Calle 100 # 15-30, Consultorio 401, Bogotá',
  calendar_provider     = 'microsoft',           -- conserva MS365 sync real
  recordatorio_habilitado = 1,
  recordatorio_1_horas  = 24,                    -- recordatorio multi-etapa 24h
  recordatorio_2_horas  = 2,                     -- + 2h antes
  antelacion_minima_horas = 12,                  -- motor de reglas
  antelacion_maxima_dias  = 30,
  fecha_actualizacion   = SYSUTCDATETIME()
WHERE id_tenant = @tenant;

-- ── 2) SERVICIOS ODONTOLÓGICOS (reconversión conservando IDs) ────────────
-- E9FAF202 = Consulta de Control (20')  -> Consulta Odontológica de Revisión (20')
UPDATE service_types SET
  nombre='Consulta de Revisión', descripcion='Revisión y control de tratamiento odontológico',
  duracion_minutos=20, buffer_minutos=5, precio=60000, activo=1, fecha_creacion=SYSUTCDATETIME()
WHERE id_tenant=@tenant AND id_service_type='E9FAF202-CDA7-4F97-B075-46001DE87CE8';

-- 517C19D8 = Consulta General (30')     -> Consulta Odontológica General (30')
UPDATE service_types SET
  nombre='Consulta Odontológica General', descripcion='Valoración diagnóstica y consulta general',
  duracion_minutos=30, buffer_minutos=5, precio=90000, activo=1, fecha_creacion=SYSUTCDATETIME()
WHERE id_tenant=@tenant AND id_service_type='517C19D8-2BF8-40AC-9F9F-7FCA9DDC7D86';

-- EFB56EB3 = Procedimiento Menor (45')  -> Limpieza Dental / Profilaxis (45')
UPDATE service_types SET
  nombre='Limpieza Dental (Profilaxis)', descripcion='Profilaxis y pulido dental',
  duracion_minutos=45, buffer_minutos=10, precio=120000, activo=1, fecha_creacion=SYSUTCDATETIME()
WHERE id_tenant=@tenant AND id_service_type='EFB56EB3-FC40-47D3-94EB-B42041AA4495';

-- AAE4102F = Laboratorio (30')          -> Ortodoncia (30')
UPDATE service_types SET
  nombre='Ortodoncia', descripcion='Ajuste y control de tratamiento de ortodoncia',
  duracion_minutos=30, buffer_minutos=10, precio=250000, activo=1, fecha_creacion=SYSUTCDATETIME()
WHERE id_tenant=@tenant AND id_service_type='AAE4102F-911F-495F-8185-D7F27C4238CF';

-- Nuevos servicios: Extracción y Blanqueamiento (si no existen por nombre)
IF NOT EXISTS (SELECT 1 FROM service_types WHERE id_tenant=@tenant AND nombre='Extracción Dental')
  INSERT INTO service_types (id_service_type,id_tenant,nombre,descripcion,duracion_minutos,buffer_minutos,precio,activo,fecha_creacion,capacidad_maxima)
  VALUES (NEWID(),@tenant,'Extracción Dental','Extracción de pieza dental o muela del juicio',45,15,200000,1,SYSUTCDATETIME(),1);
IF NOT EXISTS (SELECT 1 FROM service_types WHERE id_tenant=@tenant AND nombre='Blanqueamiento Dental')
  INSERT INTO service_types (id_service_type,id_tenant,nombre,descripcion,duracion_minutos,buffer_minutos,precio,activo,fecha_creacion,capacidad_maxima)
  VALUES (NEWID(),@tenant,'Blanqueamiento Dental','Blanqueamiento dental profesional',60,10,350000,1,SYSUTCDATETIME(),1);

-- ── 3) EQUIPO ODONTOLÓGICO (conservando IDs, ajustando rol) ──────────────
UPDATE professionals SET
  nombre='Dra. María', email='maria.odontologia@sonrisa.dental', telefono='+573001112233',
  activo=1, fecha_creacion=SYSUTCDATETIME()
WHERE id_tenant=@tenant AND id_professional='B46D5CF8-471E-4EDA-80D7-A69AD06E5F4D';

UPDATE professionals SET
  nombre='Dr. Carlos', email='carlos.odontologia@sonrisa.dental', telefono='+573001122334',
  activo=1, fecha_creacion=SYSUTCDATETIME()
WHERE id_tenant=@tenant AND id_professional='2778244C-1331-4D61-ACDF-F0709DF48B0A';

-- Cartera: ambos odontólogos atienden TODOS los servicios del tenant
INSERT INTO professional_services (id_professional, id_service_type, activo)
SELECT p.id_professional, s.id_service_type, 1
FROM professionals p CROSS JOIN service_types s
WHERE p.id_tenant=@tenant AND s.id_tenant=@tenant
  AND NOT EXISTS (SELECT 1 FROM professional_services ps
                  WHERE ps.id_professional=p.id_professional AND ps.id_service_type=s.id_service_type);

-- ── 4) HORARIOS del consultorio (lun-vie 09-18, sáb 09-13) ────────────────
DELETE FROM availability_rules WHERE id_tenant=@tenant;
INSERT INTO availability_rules (id_availability_rule, id_tenant, dia_semana, hora_inicio, hora_fin, activo, fecha_creacion) VALUES
 (NEWID(),@tenant,1,'09:00','18:00',1,SYSUTCDATETIME()),
 (NEWID(),@tenant,2,'09:00','18:00',1,SYSUTCDATETIME()),
 (NEWID(),@tenant,3,'09:00','18:00',1,SYSUTCDATETIME()),
 (NEWID(),@tenant,4,'09:00','18:00',1,SYSUTCDATETIME()),
 (NEWID(),@tenant,5,'09:00','18:00',1,SYSUTCDATETIME()),
 (NEWID(),@tenant,6,'09:00','13:00',1,SYSUTCDATETIME());

-- ── Reporte de verificación ───────────────────────────────────────────────
SELECT 'TENANT' Tipo, nombre, nombre_comercial, calendar_provider, recordatorio_habilitado,
       recordatorio_1_horas AS r1h, recordatorio_2_horas AS r2h, antelacion_minima_horas AS minh, antelacion_maxima_dias AS maxd
FROM tenants WHERE id_tenant=@tenant;
SELECT 'SERVICIO' Tipo, nombre, duracion_minutos, precio, activo FROM service_types WHERE id_tenant=@tenant ORDER BY nombre;
SELECT 'HORARIO' Tipo, dia_semana, hora_inicio, hora_fin FROM availability_rules WHERE id_tenant=@tenant ORDER BY dia_semana;
SELECT 'PROFESIONAL' Tipo, p.nombre, p.email, COUNT(ps.id_service_type) AS servicios
FROM professionals p LEFT JOIN professional_services ps ON ps.id_professional=p.id_professional
WHERE p.id_tenant=@tenant GROUP BY p.nombre, p.email;