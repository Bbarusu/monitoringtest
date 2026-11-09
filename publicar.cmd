@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

cd /d "%~dp0"

set "MODO=completo"
if /i "%~1"=="leve" set "MODO=leve"

if "%MODO%"=="leve" (
    set "ARGS_APP=-p:SelfContained=false -p:PublishSingleFile=false"
    set "ARGS_SETUP=-p:SelfContained=false -p:PublishSingleFile=true"
) else (
    set "ARGS_APP=-p:SelfContained=true -p:PublishSingleFile=false"
    set "ARGS_SETUP=-p:SelfContained=true -p:PublishSingleFile=true"
)

echo.
echo === KInfo — gerando o instalador (modo %MODO%) ===
echo.

if exist "dist" rmdir /s /q "dist"
if exist "src\MonitorPC.Setup\Payload" rmdir /s /q "src\MonitorPC.Setup\Payload"
mkdir "src\MonitorPC.Setup\Payload"

echo [1/5] Publicando o app...
dotnet publish "src\MonitorPC\MonitorPC.csproj" ^
    -c Release -r win-x64 %ARGS_APP% ^
    -o "dist\app" ^
    --nologo -v minimal
if errorlevel 1 goto :falhou

if not exist "dist\app\LibreHardwareMonitorLib.dll" (
    echo.
    echo ERRO: LibreHardwareMonitorLib.dll nao veio na publicacao.
    echo Confira se algum antivirus a removeu da pasta de build ou do cache do
    echo NuGet: veja o Historico de Protecao, restaure o arquivo se estiver la,
    echo adicione uma excecao para a pasta do projeto e rode de novo.
    goto :falhou
)

echo [2/5] Empacotando...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Compress-Archive -Path 'dist\app\*' -DestinationPath 'src\MonitorPC.Setup\Payload\app.zip' -Force"
if errorlevel 1 goto :falhou

echo [3/5] Gerando o instalador...
dotnet publish "src\MonitorPC.Setup\MonitorPC.Setup.csproj" ^
    -c Release -r win-x64 %ARGS_SETUP% ^
    -o "dist\instalador" ^
    --nologo -v minimal
if errorlevel 1 goto :falhou

echo [4/5] Reunindo o codigo-fonte...
powershell -NoProfile -ExecutionPolicy Bypass -File "codigo-unico.ps1"
if errorlevel 1 goto :falhou

echo [5/5] Gerando o manifesto de atualizacao...
powershell -NoProfile -ExecutionPolicy Bypass -File "gerar-manifesto.ps1"
if errorlevel 1 goto :falhou

echo.
echo === Pronto ===
for %%A in ("dist\instalador\KInfo-Setup.exe") do (
    echo Instalador: %%~fA
    set /a TAMANHO=%%~zA/1048576
    echo Tamanho:    !TAMANHO! MB
)
echo.
echo Para instalar, execute o arquivo acima. Ele pedira confirmacao do Windows
echo uma vez, porque escreve em Arquivos de Programas e registra a tarefa que
echo da acesso aos sensores.
echo.
echo Para publicar esta versao e as outras maquinas encontrarem:
echo   publicar-release.cmd
echo.
exit /b 0

:falhou
echo.
echo === Falhou. Veja as mensagens acima. ===
exit /b 1
