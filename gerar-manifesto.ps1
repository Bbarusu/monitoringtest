$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'dist\instalador\KInfo-Setup.exe'

if (-not (Test-Path $exe)) {
    Write-Host "ERRO: não encontrei $exe" -ForegroundColor Red
    Write-Host "Rode o publicar.cmd primeiro."
    exit 1
}

$versao = (Get-Item $exe).VersionInfo.ProductVersion

if ([string]::IsNullOrWhiteSpace($versao)) {
    Write-Host "ERRO: o executável não declara versão." -ForegroundColor Red
    exit 1
}

$versao = ($versao -split '\+')[0].Trim()

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash

$arquivoDeNotas = Join-Path $PSScriptRoot 'notas-da-versao.md'
$notas = ''
if (Test-Path $arquivoDeNotas) {
    $texto = (Get-Content $arquivoDeNotas -Raw -Encoding UTF8) -replace "`r`n", "`n"

    $corte = [regex]::Match($texto, '(?im)^##\s*Detalhes')
    if ($corte.Success) { $texto = $texto.Substring(0, $corte.Index) }

    $notas = (($texto -split "`n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    $notas = $notas.Trim()
}

$manifesto = [ordered]@{
    versao = $versao
    url    = "https://github.com/Bbarusu/kinfo-releases/releases/download/v$versao/KInfo-Setup.exe"
    sha256 = $hash
    notas  = $notas
    data   = (Get-Date -Format 'yyyy-MM-dd')
}

$destino = Join-Path $PSScriptRoot 'dist\instalador\atualizacao.json'
$json = $manifesto | ConvertTo-Json -Depth 4

[System.IO.File]::WriteAllText($destino, $json, (New-Object System.Text.UTF8Encoding $false))

Write-Host ''
Write-Host 'Manifesto gerado:' -ForegroundColor Green
Write-Host "  $destino"
Write-Host ''
Write-Host "  versão : $versao"
Write-Host "  sha256 : $hash"
Write-Host ''
Write-Host 'Para publicar, rode:' -ForegroundColor Cyan
Write-Host '  publicar-release.cmd'
Write-Host ''
Write-Host 'Não rode o gh à mão daqui. Sem --repo ele usa o repositório desta'
Write-Host 'pasta, que é o do código-fonte e é privado — a release vai para o'
Write-Host 'lugar que o app não consegue ler. O script cuida disso, anexa os'
Write-Host 'dois arquivos e não marca como pre-release, que o caminho'
Write-Host 'releases/latest ignora.'
Write-Host ''
