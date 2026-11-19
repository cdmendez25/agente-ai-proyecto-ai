@echo off
rem Banco de pruebas AgentsPoker (workbench). Ejemplos, desde PowerShell o cmd:
rem   .\run.cmd list
rem   .\run.cmd match --agent MiAgente --builtin Heuristic*3 --verbose
rem   .\run.cmd bench --agent MiAgente --builtin Heuristic*5 --matches 200
rem   .\run.cmd bench --agent MiAgente --builtin Heuristic*5 --final    (simula la mesa final)
pushd "%~dp0"
dotnet run -c Release --project tools\AgentsPoker.Sim -- %*
set "code=%ERRORLEVEL%"
popd
exit /b %code%
