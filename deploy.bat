@echo off
rem Despliegue de producción de AgendaApi con doble clic.
rem Abre PowerShell y corre el deploy completo (publica binario, levanta Docker + SQL + API,
rem conecta el túnel Cloudflare y verifica el health del dominio público).
rem Se detiene SIEMPRE al final para que puedas leer el resumen.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0scripts\deploy-production.ps1'"
set "RC=%errorlevel%"
echo.
if %RC% equ 0 (
  echo ==============================================
  echo   Deploy finalizado correctamente.
  echo   Produccion:  https://api.adamcoia.com/health
  echo ==============================================
) else (
  echo ==============================================
  echo   Fallo el despliegue (codigo %RC%). Revisa la terminal de arriba.
  echo ==============================================
)
echo.
pause
endlocal