@echo off
rem ---------------------------------------------------------------------------
rem  Atalho para o publicar-release.ps1, que faz o trabalho.
rem
rem  O trabalho todo já morou aqui, em batch, e errou: ler a versão do manifesto
rem  exigia chamar o PowerShell de dentro de um for /f, com aspas aninhadas e
rem  pipe escapado. A versão saiu vazia e a tag virou "v". Em PowerShell o JSON
rem  é lido nativamente e os argumentos vão para o gh como lista, sem nenhuma
rem  camada de aspas no meio.
rem
rem  Este arquivo fica para o duplo clique continuar funcionando.
rem ---------------------------------------------------------------------------

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publicar-release.ps1"
exit /b %errorlevel%
