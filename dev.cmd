@echo off
rem Desarrollo rapido: compila en Debug (incremental) y ejecuta.
cd /d "%~dp0"
dotnet run --project src\DataPeach -c Debug
