@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo === Trien khai ERP - Phuong an B (dung chung SQL Server) ===
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0deploy-erp-shared-db.ps1' *>&1 | Tee-Object -FilePath '%~dp0deploy.log'"
echo.
echo Hoan tat. Nhan phim bat ky de dong cua so.
pause >nul
