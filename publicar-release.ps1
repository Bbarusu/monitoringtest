# ---------------------------------------------------------------------------
#  Publica a release do KInfo no repositório público.
#
#  Duas tentativas anteriores morreram em armadilhas de linguagem, não de
#  lógica, e as duas vale deixar registradas porque convidam a repetição:
#
#  1. Em batch, ler a versão do manifesto exigia chamar o PowerShell de dentro
#     de um for /f, com aspas aninhadas e pipe escapado. A versão saiu vazia e
#     a tag virou "v".
#
#  2. Em PowerShell com $ErrorActionPreference = 'Stop', qualquer coisa que um
#     programa externo escreva no stderr vira erro terminante. O
#     "gh release view" diz "release not found" pelo stderr — que é a resposta
#     CERTA quando a release ainda não existe — e isso matava o script
#     exatamente no caso normal.
#
#  Por isso aqui o preference fica em Continue, cada chamada ao gh passa por
#  Gh(), que devolve código e saída sem transformar stderr em exceção, e os
#  cmdlets que precisam falhar alto levam -ErrorAction Stop individualmente.
#
#  Uso:  publicar-release.cmd   (ou este arquivo direto pelo PowerShell)
# ---------------------------------------------------------------------------

# Deliberadamente Continue: veja o item 2 acima.
$ErrorActionPreference = 'Continue'

# O PowerShell 5.1 ainda negocia TLS 1.0 em parte das instalações, e o GitHub
# recusa. Sem isto a conferência do fim falharia numa release publicada sem
# problema nenhum.
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

# Chama o gh e devolve código e saída juntos. Existe para o stderr do gh ser
# dado, e não acidente.
#  O nome NÃO pode ser "Gh". O PowerShell não diferencia maiúsculas e resolve
#  funções antes de programas externos, então uma função chamada Gh sequestra
#  toda chamada a "gh" — inclusive a de dentro dela mesma, que passa a ser
#  recursão em vez de chamar o GitHub CLI. Foi o que aconteceu: o script
#  imprimiu o próprio objeto de retorno no lugar da saída do gh.
#
#  Por isso também o executável é chamado pelo caminho resolvido, e não pelo
#  nome: caminho não é ambíguo com nada.
#
#  O parâmetro é declarado como lista, e não deixado para $args: argumentos que
#  começam com "-", como --repo, correm o risco de ser lidos como nome de
#  parâmetro da própria função.
function ChamarGh([string[]]$Argumentos) {
    $saida = & $script:ghExe @Argumentos 2>&1 | Out-String
    return [pscustomobject]@{ Codigo = $LASTEXITCODE; Saida = $saida.Trim() }
}

Write-Host ''
Write-Host '=== KInfo — publicando a release ===' -ForegroundColor Cyan
Write-Host ''

# --- Os dois arquivos precisam existir -------------------------------------
if (-not (Test-Path $exe))       { Parar "não encontrei $exe`nRode o publicar.cmd primeiro." }
if (-not (Test-Path $manifesto)) { Parar "não encontrei $manifesto`nRode o publicar.cmd primeiro: o manifesto sai no passo 5." }

# --- A versão vem do manifesto, que saiu do executável ---------------------
try {
    $dados = Get-Content $manifesto -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
}
catch {
    Parar ("não consegui ler $manifesto como JSON.`n" + $_.Exception.Message)
}

$versao = "$($dados.versao)".Trim()

# O padrão é conferido, e não só a presença: foi uma versão vazia que virou a
# tag "v" e fez o GitHub recusar com "tag_name is not well-formed".
if ($versao -notmatch '^\d+\.\d+\.\d+$') {
    Parar "a versão lida do manifesto não serve: '$versao'`nEsperado algo como 0.1.2. Rode o publicar.cmd de novo."
}

$tag = "v$versao"

# --- O gh precisa estar instalado e autenticado ----------------------------
# -CommandType Application é parte do conserto: sem isso o Get-Command acharia
# a própria função deste script e diria que o gh existe quando não existe.
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

# Aqui havia uma conferência de "gh auth status" que barrava a publicação.
# Ela deu falso negativo com o gh autenticado e logado — esse comando não
# devolve código zero de forma confiável entre versões — e bloqueou o caminho
# bom.
#
# É a terceira conferência prévia deste script a falhar onde a operação real
# teria funcionado, então vale a regra em vez do remendo: barrar só o que não
# dá para descobrir tentando. Arquivo que não existe e versão malformada são
# assim, porque o erro do gh para esses casos seria obscuro. Autenticação não
# é: o próprio gh diz, com todas as letras, quando falta.

Write-Host "Versão      : $versao"
Write-Host "Repositório : $repo"
Write-Host "Tag         : $tag"
Write-Host ''

# --- Uma release com esta tag já pode existir ------------------------------
#  Código 0 aqui significa "achei", que é o caso ruim. Qualquer outro código é
#  "não existe", que é o que queremos — inclusive com o gh escrevendo
#  "release not found" no stderr.
if ((ChamarGh @('release','view',$tag,'--repo',$repo)).Codigo -eq 0) {
    Parar ("a release $tag já existe em $repo.`n" +
           "Suba a versão no Directory.Build.props, rode o publicar.cmd e volte aqui.`n" +
           "Republicar o mesmo número não adianta: o app compara versões, e igual não é mais novo.")
}

# --- Rascunhos não aparecem por tag, e atrapalham -------------------------
$lista = ChamarGh @('release','list','--repo',$repo,'--limit','30')

if ($lista.Codigo -eq 0 -and $lista.Saida -match 'Draft') {
    Write-Host 'Aviso: há rascunho de release neste repositório.' -ForegroundColor Yellow
    Write-Host 'Rascunho não aparece em releases/latest e pode confundir o teste.'
    Write-Host $lista.Saida
    Write-Host ''
}

# --- Publica ---------------------------------------------------------------
#  --repo é obrigatório: sem ele o gh usaria o repositório desta pasta, que é o
#  do código-fonte e é privado — foi para lá que a primeira release foi.
#  Sem --prerelease de propósito: releases/latest ignora pre-releases.
$argumentos = @(
    'release', 'create', $tag, $exe, $manifesto,
    '--repo', $repo,
    '--title', "KInfo $versao"
)

if (Test-Path $notas) { $argumentos += @('--notes-file', $notas) }
else                  { $argumentos += @('--notes', "Versão $versao.") }

Write-Host 'Enviando (o instalador tem ~137 MB, leva um tempo)…'
Write-Host ''

# Transmitido linha a linha para o progresso aparecer durante o envio, e
# convertido para texto para o stderr do gh não sair pintado de erro.
& $ghExe @argumentos 2>&1 | ForEach-Object { Write-Host $_ }
$codigo = $LASTEXITCODE

if ($codigo -ne 0) {
    Parar ("o gh terminou com código $codigo. A mensagem dele está acima.`n`n" +
           "Se ela falar em autenticação, rode:  gh auth login`n" +
           "Se falar em permissão, confira que $repo é seu e é público.")
}

# --- Confere que ficou de pé no caminho que o app consulta -----------------
#  Publicar sem erro não garante que o app encontre: rascunho e pre-release
#  somem de releases/latest, e é por esse caminho que o app pergunta.
$alvo = "https://github.com/$repo/releases/latest/download/atualizacao.json"

Write-Host ''
Write-Host 'Conferindo pelo caminho que o app usa…'

# Esta conferência NÃO reprova a publicação, e isso é deliberado.
#
# Quem decide se publicou é o gh, que já respondeu zero e imprimiu a URL da
# release. O ponteiro "latest" do GitHub é atualizado depois, e demora: na
# 0.1.3 ele levou mais que os vinte segundos que esta conferência esperava, e
# o script reprovou uma release que estava perfeita — o terceiro caso nesta
# sessão de eu transformar uma verificação opcional em veredito.
#
# Então aqui ela só informa. Confirmou, ótimo; não confirmou, diz onde olhar.
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
