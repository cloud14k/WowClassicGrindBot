@echo off

cd /D "%~dp0"
start "" "http://localhost:5000"
dotnet run --configuration Release -e:ASPNETCORE_ENVIRONMENT=Development -- %*

pause
