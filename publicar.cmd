@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

rem ---------------------------------------------------------------------------
rem  Gera o instalador do KInfo.
rem
rem  Sem argumento, sai um executavel unico que roda em qualquer Windows 10 ou
rem  11 de 64 bits, sem exigir nada instalado — o .NET viaja dentro. Fica perto
rem  de 130 MB.
rem
rem  Com "leve", sai um instalador de poucos megabytes que exige o .NET 8
rem  Desktop Runtime na maquina de destino.
rem
rem  Uso:  publicar.cmd          ou  publicar.cmd leve
rem ---------------------------------------------------------------------------

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

rem --- Limpa a saida anterior, para nao empacotar sobra de build antigo ------
if exist "dist" rmdir /s /q "dist"
if exist "src\MonitorPC.Setup\Payload" rmdir /s /q "src\MonitorPC.Setup\Payload"
mkdir "src\MonitorPC.Setup\Payload"

rem --- 1. Publica o app -----------------------------------------------------
echo [1/5] Publicando o app...
dotnet publish "src\MonitorPC\MonitorPC.csproj" ^
    -c Release -r win-x64 %ARGS_APP% ^
    -o "dist\app" ^
    --nologo -v minimal
if errorlevel 1 goto :falhou

rem A biblioteca de sensores precisa estar ali: sem ela o app nem abre. Ja
rem aconteceu de ela desaparecer da pasta de build, e a causa nunca foi
rem confirmada — por isso a conferencia fica, mesmo sem diagnostico fechado.
if not exist "dist\app\LibreHardwareMonitorLib.dll" (
    echo.
    echo ERRO: LibreHardwareMonitorLib.dll nao veio na publicacao.
    echo Confira se algum antivirus a removeu da pasta de build ou do cache do
    echo NuGet: veja o Historico de Protecao, restaure o arquivo se estiver la,
    echo adicione uma excecao para a pasta do projeto e rode de novo.
    goto :falhou
)

rem --- 2. Empacota o app dentro do instalador -------------------------------
echo [2/5] Empacotando...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Compress-Archive -Path 'dist\app\*' -DestinationPath 'src\MonitorPC.Setup\Payload\app.zip' -Force"
if errorlevel 1 goto :falhou

rem --- 3. Publica o instalador com o app dentro -----------------------------
echo [3/5] Gerando o instalador...
dotnet publish "src\MonitorPC.Setup\MonitorPC.Setup.csproj" ^
    -c Release -r win-x64 %ARGS_SETUP% ^
    -o "dist\instalador" ^
    --nologo -v minimal
if errorlevel 1 goto :falhou

rem --- 4. Junta o codigo num arquivo so, ao lado do instalador -------------
echo [4/5] Reunindo o codigo-fonte...
powershell -NoProfile -ExecutionPolicy Bypass -File "codigo-unico.ps1"
if errorlevel 1 goto :falhou

rem --- 5. Gera o manifesto de atualizacao ----------------------------------
rem  O app le este arquivo para saber se existe versao nova, e ele carrega o
rem  SHA-256 do instalador — a unica integridade real que temos, porque o
rem  executavel nao e assinado. O hash sai daqui, do arquivo recem-compilado,
rem  e nunca da mao de ninguem.
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
