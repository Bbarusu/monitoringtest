# ---------------------------------------------------------------------------
#  Junta todo o codigo-fonte do KInfo num arquivo so, para leitura e revisao.
#
#  Chamado pelo publicar.cmd, para o arquivo nunca ficar defasado em relacao
#  ao instalador que saiu junto com ele.
#
#  O texto do cabecalho mora em arte\cabecalho-codigo.md, e nao aqui: assim
#  este script fica em ASCII puro e a prosa acentuada vive num arquivo lido
#  explicitamente como UTF-8, sem depender da pagina de codigo do console.
#
#      powershell -ExecutionPolicy Bypass -File codigo-unico.ps1
# ---------------------------------------------------------------------------

param(
    [string]$Destino = "dist\instalador\KInfo-codigo-completo.md"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$extensoes = @(".cs", ".xaml", ".csproj", ".manifest", ".json", ".cmd", ".ps1", ".py", ".md")

# Saida de build, pacote intermediario e o proprio arquivo gerado ficam fora.
$ignorar = @("\bin\", "\obj\", "\dist\", "\Payload\", "\.git\", "\.vs\")

# Ordem de leitura: o que explica antes do que executa.
$prioridade = @(
    "README.md", "DESIGN.md",
    ".csproj", ".manifest", "App.xaml", "App.xaml.cs",
    "\Models\", "\Services\", "\Views\",
    ".cmd", ".ps1", ".py"
)

function Peso($caminho) {
    for ($i = 0; $i -lt $prioridade.Count; $i++) {
        if ($caminho -like "*$($prioridade[$i])*") { return $i }
    }
    return $prioridade.Count
}

function Linguagem($extensao) {
    switch ($extensao) {
        ".cs"       { "csharp" }
        ".xaml"     { "xml" }
        ".csproj"   { "xml" }
        ".manifest" { "xml" }
        ".json"     { "json" }
        ".cmd"      { "bat" }
        ".ps1"      { "powershell" }
        ".py"       { "python" }
        ".md"       { "markdown" }
        default     { "" }
    }
}

$arquivos = Get-ChildItem -Recurse -File |
    Where-Object {
        $caminho = $_.FullName
        ($extensoes -contains $_.Extension.ToLower()) -and
        -not ($ignorar | Where-Object { $caminho -like "*$_*" }) -and
        ($_.Name -ne "cabecalho-codigo.md")
    } |
    Sort-Object @{ Expression = { Peso($_.FullName) } }, FullName

$texto = New-Object System.Text.StringBuilder
$raiz = (Get-Location).Path
$total = 0

foreach ($a in $arquivos) {
    $total += (Get-Content -LiteralPath $a.FullName -Encoding UTF8 | Measure-Object -Line).Lines
}

$cabecalho = Get-Content -LiteralPath "arte\cabecalho-codigo.md" -Encoding UTF8 -Raw
[void]$texto.AppendLine($cabecalho)

$commit = ""
try { $commit = (git rev-parse --short HEAD 2>$null) } catch { }

[void]$texto.AppendLine("## Este arquivo")
[void]$texto.AppendLine("")
[void]$texto.AppendLine("- Gerado em $(Get-Date -Format 'dd/MM/yyyy HH:mm')")
if ($commit) { [void]$texto.AppendLine("- Commit ``$commit``") }
[void]$texto.AppendLine("- $($arquivos.Count) arquivos, $total linhas")
[void]$texto.AppendLine("- Nao inclui o icone (``kinfo.ico``) nem a previa em PNG, que sao binarios")
[void]$texto.AppendLine("")
[void]$texto.AppendLine("---")
[void]$texto.AppendLine("")
[void]$texto.AppendLine("## Indice")
[void]$texto.AppendLine("")

foreach ($a in $arquivos) {
    [void]$texto.AppendLine("- ``$($a.FullName.Substring($raiz.Length + 1))``")
}

foreach ($a in $arquivos) {
    $relativo = $a.FullName.Substring($raiz.Length + 1)
    $conteudo = Get-Content -LiteralPath $a.FullName -Encoding UTF8 -Raw
    if ($null -eq $conteudo) { $conteudo = "" }

    [void]$texto.AppendLine("")
    [void]$texto.AppendLine("---")
    [void]$texto.AppendLine("")
    [void]$texto.AppendLine("## $relativo")
    [void]$texto.AppendLine("")
    $ext = $a.Extension.ToLower()
    [void]$texto.AppendLine('```' + (Linguagem $ext))
    [void]$texto.AppendLine($conteudo.TrimEnd())
    [void]$texto.AppendLine('```')
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destino) | Out-Null
[System.IO.File]::WriteAllText(
    (Join-Path $raiz $Destino), $texto.ToString(), (New-Object System.Text.UTF8Encoding $true))

Write-Host "Codigo completo: $Destino  ($($arquivos.Count) arquivos, $total linhas)"
