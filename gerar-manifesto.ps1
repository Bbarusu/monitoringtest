# ---------------------------------------------------------------------------
#  Gera o atualizacao.json ao lado do instalador.
#
#  Chamado pelo publicar.cmd. É o arquivo que o app baixa para saber se existe
#  versão nova, e ele carrega o SHA-256 do instalador — que é a única
#  integridade real do projeto, já que não assinamos o executável.
#
#  Por isso o hash é calculado aqui, do arquivo que acabou de sair do build, e
#  nunca digitado. Hash copiado à mão erra uma vez e o app passa a recusar toda
#  atualização sem dizer por quê.
#
#  A versão também é lida do executável, não escrita aqui: a fonte única é o
#  Directory.Build.props na raiz.
# ---------------------------------------------------------------------------

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

# O SDK pode acrescentar "+<hash do commit>"; o que interessa está antes.
$versao = ($versao -split '\+')[0].Trim()

$hash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash

# As notas da versão, se existirem. Opcional de propósito: faltar nota não
# pode impedir de publicar uma correção.
$arquivoDeNotas = Join-Path $PSScriptRoot 'notas-da-versao.md'
$notas = if (Test-Path $arquivoDeNotas) {
    (Get-Content $arquivoDeNotas -Raw -Encoding UTF8).Trim()
} else {
    ''
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

# Sem BOM: JSON com marca de ordem de bytes quebra leitor estrito, e não há
# ganho nenhum em pôr uma.
[System.IO.File]::WriteAllText($destino, $json, (New-Object System.Text.UTF8Encoding $false))

Write-Host ''
Write-Host 'Manifesto gerado:' -ForegroundColor Green
Write-Host "  $destino"
Write-Host ''
Write-Host "  versão : $versao"
Write-Host "  sha256 : $hash"
Write-Host ''
Write-Host 'Para publicar, crie a release com a TAG v' -NoNewline
Write-Host "$versao" -NoNewline
Write-Host ' no repositório público e anexe os dois arquivos:'
Write-Host ''
Write-Host "  gh release create v$versao ``"
Write-Host "    `"$exe`" ``"
Write-Host "    `"$destino`" ``"
Write-Host "    --repo Bbarusu/kinfo-releases --title `"KInfo $versao`" --notes-file notas-da-versao.md"
Write-Host ''
Write-Host 'A url dentro do manifesto aponta para essa tag, então ela precisa'
Write-Host 'bater. O app lê o manifesto por releases/latest/download.'
Write-Host ''
