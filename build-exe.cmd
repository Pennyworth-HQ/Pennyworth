@echo off
cd /d "%~dp0"
echo Gerando executavel do Pennyworth... > build-exe.log
dotnet --version >> build-exe.log 2>&1
dotnet publish src\Pennyworth.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish >> build-exe.log 2>&1
echo EXITCODE=%ERRORLEVEL% >> build-exe.log
