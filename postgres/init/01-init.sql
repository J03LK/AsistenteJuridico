-- ============================================================
-- 01-init.sql — Inicialización de PostgreSQL
-- Se ejecuta automáticamente al crear el contenedor por primera vez.
-- ============================================================

-- Extensiones útiles
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";  -- Para búsqueda de texto

-- Confirmar inicialización
SELECT 'Base de datos asistente_juridico inicializada correctamente.' AS mensaje;
