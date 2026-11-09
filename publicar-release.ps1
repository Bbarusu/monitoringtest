$ErrorActionPreference = 'Continue'

try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$repo      = 'Bbarusu/kinfo-releases'
$exe       = Join-Path $PSScriptRoot 'dist\instalador\KInfo-Setup.exe'
$manifesto = Join-Path $PSScriptRoot 'dist\instalador\atualizacao.json'
$catalogo  = Join-Path $PSScriptRoot 'src\MonitorPC\Dados\catalogo.json'
$notas     = Join-Path $PSScriptRoot 'notas-da-versao.md'

function Parar($mensagem) {
    Write-Host ''
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    Write-Host ''
    exit 1
}

function ChamarGh([string[]]$Argumentos) {
    $saida = & $script:ghExe @Argumentos 2>&1 | Out-String
    return [pscustomobject]@{ Codigo = $LASTEXITCODE; Saida = $saida.Trim() }
}

Write-Host ''
Write-Host '=== KInfo — publicando a release ===' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $exe))       { Parar "não encontrei $exe`nRode o publicar.cmd primeiro." }
if (-not (Test-Path $manifesto)) { Parar "não encontrei $manifesto`nRode o publicar.cmd primeiro: o manifesto sai no passo 5." }

try {
    $dados = Get-Content $manifesto -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
}
catch {
    Parar ("não consegui ler $manifesto como JSON.`n" + $_.Exception.Message)
}

$versao = "$($dados.versao)".Trim()

if ($versao -notmatch '^\d+\.\d+\.\d+$') {
    Parar "a versão lida do manifesto não serve: '$versao'`nEsperado algo como 0.1.2. Rode o publicar.cmd de novo."
}

$tag = "v$versao"

$comandoGh = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue
$script:ghExe = $comandoGh.Source

if (-not $comandoGh) {
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

Write-Host "Versão      : $versao"
Write-Host "Repositório : $repo"
Write-Host "Tag         : $tag"
Write-Host ''

if ((ChamarGh @('release','view',$tag,'--repo',$repo)).Codigo -eq 0) {
    Parar ("a release $tag já existe em $repo.`n" +
           "Suba a versão no Directory.Build.props, rode o publicar.cmd e volte aqui.`n" +
           "Republicar o mesmo número não adianta: o app compara versões, e igual não é mais novo.")
}

$lista = ChamarGh @('release','list','--repo',$repo,'--limit','30')

if ($lista.Codigo -eq 0 -and $lista.Saida -match 'Draft') {
    Write-Host 'Aviso: há rascunho de release neste repositório.' -ForegroundColor Yellow
    Write-Host 'Rascunho não aparece em releases/latest e pode confundir o teste.'
    Write-Host $lista.Saida
    Write-Host ''
}

$argumentos = @(
    'release', 'create', $tag, $exe, $manifesto,
    '--repo', $repo,
    '--title', "KInfo $versao"
)

if (Test-Path $catalogo) { $argumentos += @($catalogo) }
else { Write-Host "Aviso: não encontrei $catalogo; a release sai sem o catálogo de peças." -ForegroundColor Yellow }

if (Test-Path $notas) { $argumentos += @('--notes-file', $notas) }
else                  { $argumentos += @('--notes', "Versão $versao.") }

Write-Host 'Enviando (o instalador tem ~137 MB, leva um tempo)…'
Write-Host ''

& $ghExe @argumentos 2>&1 | ForEach-Object { Write-Host $_ }
$codigo = $LASTEXITCODE

if ($codigo -ne 0) {
    Parar ("o gh terminou com código $codigo. A mensagem dele está acima.`n`n" +
           "Se ela falar em autenticação, rode:  gh auth login`n" +
           "Se falar em permissão, confira que $repo é seu e é público.")
}

$alvo = "https://github.com/$repo/releases/latest/download/atualizacao.json"

Write-Host ''
Write-Host 'Conferindo pelo caminho que o app usa…'

$publicado = $null

foreach ($tentativa in 1..6) {
    try {
        $r = Invoke-WebRequest -Uri $alvo -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        $publicado = "$(($r.Content | ConvertFrom-Json).versao)".Trim()
        break
    }
    catch {
        if ($tentativa -lt 6) { Start-Sleep -Seconds 10 }
    }
}

Write-Host ''

if ($publicado -eq $versao) {
    Write-Host "=== Publicado: $versao está no ar ===" -ForegroundColor Green
}
elseif ($publicado) {
    Write-Host "=== Publicado, mas confira ===" -ForegroundColor Yellow
    Write-Host ''
    Write-Host "O caminho releases/latest devolve a versão '$publicado', e não '$versao'."
    Write-Host 'Há outra release marcada como a mais recente — veja se alguma ficou'
    Write-Host 'como rascunho ou pre-release:'
    Write-Host "  https://github.com/$repo/releases"
}
else {
    Write-Host "=== Release $tag criada ===" -ForegroundColor Green
    Write-Host ''
    Write-Host 'O ponteiro "latest" do GitHub ainda não respondeu, o que é normal no'
    Write-Host 'primeiro minuto. Confira daqui a pouco abrindo:'
    Write-Host "  $alvo"
    Write-Host ''
    Write-Host 'Se continuar sem responder depois de alguns minutos, a release pode ter'
    Write-Host 'ficado como rascunho ou pre-release:'
    Write-Host "  https://github.com/$repo/releases"
}

Write-Host ''
Write-Host 'Nas máquinas com a versão anterior, use "Procurar atualizações"'
Write-Host 'no menu da bandeja.'
Write-Host ''
exit 0
