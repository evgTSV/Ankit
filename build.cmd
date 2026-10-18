@echo off
setlocal

cd /d "%~dp0"
dotnet fsi Scripts\build.fsx -- %*
