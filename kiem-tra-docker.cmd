@echo off
cd /d "%~dp0"
echo === docker version === > docker-diag.txt
docker version >> docker-diag.txt 2>&1
echo === docker context === >> docker-diag.txt
docker context ls >> docker-diag.txt 2>&1
echo === docker ps -a === >> docker-diag.txt
docker ps -a --format "{{.Names}} | {{.Image}} | {{.Status}} | {{.Ports}}" >> docker-diag.txt 2>&1
echo === docker images === >> docker-diag.txt
docker images --format "{{.Repository}}:{{.Tag}}" >> docker-diag.txt 2>&1
type docker-diag.txt
echo.
echo Da ghi ket qua vao docker-diag.txt. Nhan phim bat ky de dong.
pause >nul
