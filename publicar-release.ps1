# ---------------------------------------------------------------------------
#  Publica a release do KInfo no repositório público.
#
#  Isto já foi um .cmd, e o .cmd errou: ler a versão do manifesto exigia
#  chamar o PowerShell de dentro de um for /f, com aspas aninhadas, pipe
#  escapado e continuação de linha. A versão saiu vazia, a conferência que
#  deveria abortar não disparou, e o script pediu ao GitHub uma tag "v".
#
#  Aqui o JSON é lido nativamente e os argumentos vão para o gh como lista,
#  sem nenhuma camada de aspas no meio. E cada conferência que falha para o
#  script — a que não parou antes foi exatamente a que deixou o erro passar.
#
#  Uso:  publicar-release.cmd   (ou este arquivo direto pelo PowerShell)
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

# O PowerShell 5.1 ainda negocia TLS 1.0 por padrão em parte das instalações, e
# o GitHub recusa. Sem isto a conferência do fim falha com erro de conexão numa
# release que foi publicada sem problema nenhum.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$repo      = 'Bbarusu/kinfo-releases'
$exe       = Join-Path $PSScriptRoot 'dist\instalador\KInfo-Setup.exe'
$manifesto = Join-Path $PSScriptRoot 'dist\instalador\atualizacao.json'
$notas     = Join-Path $PSScriptRoot 'notas-da-versao.md'

function Parar($mensagem) {
    Write-Host ''
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host ''
Write-Host '=== KInfo — publicando a release ===' -ForegroundColor Cyan
Write-Host ''

# --- Os dois arquivos precisam existir -------------------------------------
if (-not (Test-Path $exe))       { Parar "não encontrei $exe`nRode o publicar.cmd primeiro." }
if (-not (Test-Path $manifesto)) { Parar "não encontrei $manifesto`nRode o publicar.cmd primeiro: o manifesto sai no passo 5." }

# --- A versão vem do manifesto, que saiu do executável ---------------------
try {
    $dados = Get-Content $manifesto -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    Parar "não consegui ler $manifesto como JSON.`n$($_.Exception.Message)"
}

$versao = "$($dados.versao)".Trim()

# O padrão é conferido, e não só a presença: foi uma versão vazia que virou
# a tag "v" e fez o GitHub recusar com "tag_name is not well-formed".
if ($versao -notmatch '^\d+\.\d+\.\d+$') {
    Parar "a versão lida do manifesto não serve: '$versao'`nEsperado algo como 0.1.2. Rode o publicar.cmd de novo."
}

$tag = "v$versao"

# --- O gh precisa estar instalado e autenticado ----------------------------
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host 'O GitHub CLI (gh) não está instalado ou não está no PATH.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Dá para publicar pelo navegador:'
    Write-Host "  https://github.com/$repo/releases/new"
    Write-Host "  tag: $tag"
    Write-Host '  anexe os dois arquivos:'
    Write-Host "    $exe"
    Write-Host "    $manifesto"
    Write-Host '  NÃO marque como pre-release nem salve como rascunho — o caminho'
    Write-Host '  releases/latest, que o app consulta, ignora os dois.'
    Write-Host ''
    exit 1
}

& gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Parar "o gh não está autenticado. Rode uma vez:  gh auth login" }

Write-Host "Versão      : $versao"
Write-Host "Repositório : $repo"
Write-Host "Tag         : $tag"
Write-Host ''

# --- Uma release com esta tag já pode existir ------------------------------
& gh release view $tag --repo $repo 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    # Os parênteses são obrigatórios: sem eles o PowerShell passaria "+" e o
    # segundo trecho como argumentos soltos, e a mensagem sairia cortada.
    Parar ("a release $tag já existe em $repo.`n" +
           "Suba a versão no Directory.Build.props, rode o publicar.cmd e volte aqui.`n" +
           "Republicar o mesmo número não adianta: o app compara versões, e igual não é mais novo.")
}

# --- Publica ---------------------------------------------------------------
#  --repo é obrigatório: sem ele o gh usaria o repositório desta pasta, que é
#  o do código-fonte e é privado — foi para lá que a primeira release foi.
#  Sem --prerelease de propósito: releases/latest ignora pre-releases.
$argumentos = @(
    'release', 'create', $tag, $exe, $manifesto,
    '--repo', $repo,
    '--title', "KInfo $versao"
)

if (Test-Path $notas) { $argumentos += @('--notes-file', $notas) }
else                  { $argumentos += @('--notes', "Versão $versao.") }

Write-Host 'Enviando (o instalador tem ~137 MB, leva um tempo)…'
& gh @argumentos

if ($LASTEXITCODE -ne 0) { Parar "o gh falhou. A mensagem dele está acima." }

# --- Confere que ficou de pé no caminho que o app consulta -----------------
#  Publicar sem erro não garante que o app encontre: rascunho e pre-release
#  somem de releases/latest, e é por esse caminho que o app pergunta.
$alvo = "https://github.com/$repo/releases/latest/download/atualizacao.json"

Write-Host ''
Write-Host 'Conferindo pelo caminho que o app usa…'

try {
    $r = Invoke-WebRequest -Uri $alvo -UseBasicParsing -TimeoutSec 30
    $publicado = ($r.Content | ConvertFrom-Json).versao

    if ("$publicado".Trim() -eq $versao) {
        Write-Host ''
        Write-Host "=== Publicado: $versao está no ar ===" -ForegroundColor Green
        Write-Host ''
        Write-Host 'Nas máquinas com a versão anterior, use "Procurar atualizações"'
        Write-Host 'no menu da bandeja.'
        Write-Host ''
        exit 0
    }

    Parar "o caminho releases/latest devolve a versão '$publicado', e não '$versao'.`nHá outra release marcada como a mais recente."
}
catch {
    Parar ("a release foi criada, mas $alvo não responde.`n" +
           "Quase sempre é rascunho ou pre-release: abra a release em`n" +
           "https://github.com/$repo/releases e publique como versão normal.")
}
