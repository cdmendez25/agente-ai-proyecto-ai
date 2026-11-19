@echo off
rem Laboratorio de entrenamiento (proyecto Entrenamiento). Ejemplos, desde PowerShell o cmd:
rem   .\entrenar.cmd          cada prueba corre 30 s
rem   .\entrenar.cmd 300      cada prueba corre 300 s (resultados mas precisos)
rem   .\entrenar.cmd 30 --final   la prueba B simula la MESA FINAL (solo gana el 1.o)
pushd "%~dp0"
dotnet run -c Release --project Entrenamiento -- %*
set "code=%ERRORLEVEL%"
popd
exit /b %code%
