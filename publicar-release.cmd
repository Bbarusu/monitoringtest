@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

rem ---------------------------------------------------------------------------
rem  Publica a release do KInfo no repositorio publico.
rem
rem  Separado do publicar.cmd de proposito: compilar e um ato repetido, publicar
rem  e uma decisao. Mas rodar o gh a mao erra facil, e errou — o gh usa o
rem  repositorio da pasta atual quando nao se passa --repo, e como a gente roda
rem  de dentro do codigo-fonte, a release foi parar no repositorio privado, sem
rem  arquivo nenhum e marcada como pre-release. As tres coisas que fazem o app
rem  nao encontrar nada.
rem
rem  Este script fecha as tres: repositorio explicito, os dois arquivos
rem  anexados, e release normal.
rem
rem  Uso:  publicar-release.cmd
rem ---------------------------------------------------------------------------

cd /d "%~dp0"

set "REPO=Bbarusu/kinfo-releases"
set "EXE=dist\instalador\KInfo-Setup.exe"
set "MANIFESTO=dist\instalador\atualizacao.json"

echo.
echo === KInfo — publicando a release ===
echo.

rem --- Os dois arquivos precisam existir -------------------------------------
if not exist "%EXE%" (
    echo ERRO: nao encontrei %EXE%
    echo Rode o publicar.cmd primeiro.
    exit /b 1
)

if not exist "%MANIFESTO%" (
    echo ERRO: nao encontrei %MANIFESTO%
    echo Rode o publicar.cmd primeiro: o manifesto sai no passo 5.
    exit /b 1
)

rem --- A versao vem do manifesto, que a saiu do executavel -------------------
for /f "usebackq delims=" %%V in (`powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "(Get-Content '%MANIFESTO%' -Raw -Encoding UTF8 ^| ConvertFrom-Json).versao"`) do set "VERSAO=%%V"

if "!VERSAO!"=="" (
    echo ERRO: nao consegui ler a versao de %MANIFESTO%
    exit /b 1
)

rem --- O gh precisa estar instalado e autenticado ----------------------------
where gh >nul 2>&1
if errorlevel 1 (
    echo ERRO: o GitHub CLI ^(gh^) nao esta instalado ou nao esta no PATH.
    echo.
    echo Alternativa pelo navegador:
    echo   https://github.com/%REPO%/releases/new
    echo   tag: v!VERSAO!  — anexe os dois arquivos abaixo
    echo   %EXE%
    echo   %MANIFESTO%
    echo   NAO marque como pre-release nem salve como rascunho.
    exit /b 1
)

gh auth status >nul 2>&1
if errorlevel 1 (
    echo ERRO: o gh nao esta autenticado. Rode uma vez:  gh auth login
    exit /b 1
)

echo Versao      : !VERSAO!
echo Repositorio : %REPO%
echo Tag         : v!VERSAO!
echo.

rem --- Uma release com esta tag ja pode existir ------------------------------
gh release view "v!VERSAO!" --repo "%REPO%" >nul 2>&1
if not errorlevel 1 (
    echo ERRO: a release v!VERSAO! ja existe em %REPO%.
    echo.
    echo Suba a versao no Directory.Build.props, rode o publicar.cmd de novo e
    echo volte aqui. Republicar a mesma versao nao adianta: o app compara
    echo numeros, e igual nao e mais novo.
    exit /b 1
)

rem --- Publica ---------------------------------------------------------------
rem  --repo e obrigatorio aqui: sem ele o gh usaria o repositorio desta pasta,
rem  que e o do codigo-fonte e e privado. Sem --prerelease de proposito: o
rem  caminho releases/latest, que o app consulta, ignora pre-releases.
if exist "notas-da-versao.md" (
    gh release create "v!VERSAO!" "%EXE%" "%MANIFESTO%" ^
        --repo "%REPO%" --title "KInfo !VERSAO!" --notes-file "notas-da-versao.md"
) else (
    gh release create "v!VERSAO!" "%EXE%" "%MANIFESTO%" ^
        --repo "%REPO%" --title "KInfo !VERSAO!" --notes "Versao !VERSAO!."
)

if errorlevel 1 (
    echo.
    echo === Falhou. Veja a mensagem acima. ===
    exit /b 1
)

echo.
echo === Publicado ===
echo.
echo Confira abrindo no navegador — se o JSON aparecer, o app encontra:
echo   https://github.com/%REPO%/releases/latest/download/atualizacao.json
echo.
exit /b 0
