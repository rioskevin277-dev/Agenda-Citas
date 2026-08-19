@echo off
rem Despliegue de producción de AgendaApi con doble clic.
rem Abre PowerShell, eleva permisos si hace falta y corre el deploy completo.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0scripts\deploy-production.ps1'"
if errorlevel 1 (
  echo.
  echo Fallo el despliegue. Revisa la terminal.
  pause
)