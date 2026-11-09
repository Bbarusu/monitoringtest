$ErrorActionPreference = 'Continue'
$linhas = New-Object System.Collections.Generic.List[string]

function Secao($titulo) {
    $linhas.Add('')
    $linhas.Add('=== ' + $titulo + ' ===')
}

function Diga($texto) { $linhas.Add([string]$texto) }

function Despejar($objeto, $vazio) {
    if ($null -eq $objeto -or ($objeto -is [array] -and $objeto.Count -eq 0)) {
        Diga $vazio
    } else {
        Diga (($objeto | Format-List | Out-String).Trim())
    }
}

Diga 'KInfo — coleta de diagnóstico dos sensores'
Diga ('Gerado em ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

Secao 'Elevação deste PowerShell'

$identidade = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevado = (New-Object Security.Principal.WindowsPrincipal($identidade)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Diga ('Administrador : ' + $elevado)
Diga ('Usuário       : ' + $identidade.Name)

if (-not $elevado) {
    Diga ''
    Diga 'ATENÇÃO: sem elevação, parte da coleta volta vazia por falta de'
    Diga 'permissão — e não por ausência do problema. Rode de novo como'
    Diga 'administrador para o arquivo valer.'
}

Secao 'Máquina e Windows'

Despejar (Get-CimInstance Win32_ComputerSystem |
    Select-Object Manufacturer, Model, SystemType) '(não disponível)'
Despejar (Get-CimInstance Win32_OperatingSystem |
    Select-Object Caption, Version, BuildNumber, OSArchitecture) '(não disponível)'
Despejar (Get-CimInstance Win32_Processor |
    Select-Object Name, Manufacturer) '(não disponível)'

Secao 'Tarefa agendada do KInfo'
Diga 'RunLevel = Highest é o que dá acesso aos sensores. Qualquer outra coisa'
Diga 'aqui explica a falha sozinha.'
Diga ''

$tarefa = Get-ScheduledTask -TaskName 'KInfo' -ErrorAction SilentlyContinue

if ($null -eq $tarefa) {
    Diga 'A tarefa "KInfo" NÃO existe. O instalador não chegou a registrá-la.'
} else {
    Despejar ($tarefa.Principal |
        Select-Object UserId, LogonType, RunLevel) '(sem principal)'
    Despejar ($tarefa.Actions |
        Select-Object Execute, Arguments, WorkingDirectory) '(sem ações)'
    Despejar (Get-ScheduledTaskInfo -TaskName 'KInfo' -ErrorAction SilentlyContinue |
        Select-Object LastRunTime, LastTaskResult, NumberOfMissedRuns) '(sem histórico)'
}

Secao 'Bloqueios de driver do Windows'
Diga 'Estes dois recusam o driver SEM pôr nada em quarentena — por isso o'
Diga 'histórico do Defender pode estar limpo e a leitura ainda falhar.'
Diga ''

$hvci = Get-ItemProperty -Path `
    'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' `
    -ErrorAction SilentlyContinue

Diga ('Integridade de Memória (HVCI) Enabled : ' +
    $(if ($null -ne $hvci) { $hvci.Enabled } else { '(chave ausente)' }))

$ci = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config' `
    -ErrorAction SilentlyContinue

Diga ('VulnerableDriverBlocklistEnable      : ' +
    $(if ($null -ne $ci) { $ci.VulnerableDriverBlocklistEnable } else { '(valor ausente = padrão, hoje ligado)' }))

Despejar (Get-CimInstance -ClassName Win32_DeviceGuard `
    -Namespace 'root\Microsoft\Windows\DeviceGuard' -ErrorAction SilentlyContinue |
    Select-Object SecurityServicesConfigured, SecurityServicesRunning,
                  CodeIntegrityPolicyEnforcementStatus) '(DeviceGuard não respondeu)'

Despejar (Confirm-SecureBootUEFI -ErrorAction SilentlyContinue) '(Secure Boot não respondeu)'

Secao 'Log CodeIntegrity do Windows'
Diga 'Esta é a fonte autoritativa: quando o Windows recusa carregar um driver,'
Diga 'o motivo fica aqui. Vazio aqui quer dizer que não houve recusa.'
Diga ''

$eventos = Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' `
    -MaxEvents 40 -ErrorAction SilentlyContinue

if ($null -eq $eventos -or $eventos.Count -eq 0) {
    Diga '(nenhum evento — ou o log está desativado, ou nada foi recusado)'
} else {
    foreach ($e in $eventos) {
        Diga ('[' + $e.TimeCreated + '] Id=' + $e.Id + ' ' +
            ($e.Message -replace '\s+', ' '))
    }
}

Secao 'Defender — detecções registradas'
Diga 'Lido por comando porque a tela de histórico pode ter sido limpa.'
Diga ''

Despejar (Get-MpThreatDetection -ErrorAction SilentlyContinue |
    Sort-Object InitialDetectionTime -Descending |
    Select-Object -First 15 InitialDetectionTime, ThreatID, Resources
    ) '(nenhuma detecção registrada)'

Despejar (Get-MpThreat -ErrorAction SilentlyContinue |
    Select-Object -First 15 ThreatName, SeverityID, IsActive
    ) '(nenhuma ameaça no histórico)'

Despejar (Get-MpPreference -ErrorAction SilentlyContinue |
    Select-Object ExclusionPath, ExclusionProcess, ExclusionExtension
    ) '(não foi possível ler as exclusões)'

Secao 'Log do sistema — instalação e início de serviços'
Diga 'O evento 7045 registra todo serviço instalado no sistema; 7000 e 7026'
Diga 'registram serviço e driver que não iniciaram, com o erro do Windows.'
Diga ''
Diga 'É a pergunta que separa os dois casos: se houver um 7045 do WinRing0,'
Diga 'a biblioteca criou o serviço e a falha está no carregamento. Se não'
Diga 'houver nenhum, ela nunca chegou a criar — e o problema é antes, na'
Diga 'escrita do arquivo.'
Diga ''

$servicos = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 7000, 7026, 7045 } `
    -MaxEvents 80 -ErrorAction SilentlyContinue

if ($null -eq $servicos -or $servicos.Count -eq 0) {
    Diga '(nenhum evento 7000/7026/7045 no log do sistema)'
} else {
    foreach ($e in $servicos) {
        Diga ('[' + $e.TimeCreated + '] Id=' + $e.Id + ' ' +
            ($e.Message -replace '\s+', ' '))
    }
}

Secao 'Serviço e arquivo do driver'
Diga 'Arquivo ausente = não foi escrito ou foi apagado. Arquivo presente com'
Diga 'serviço parado = foi escrito e recusado. São problemas diferentes.'
Diga ''

Despejar (Get-Service -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'ring0|hardwaremonitor|kinfo' } |
    Select-Object Name, Status, StartType) '(nenhum serviço parecido)'

foreach ($nome in @('WinRing0_1_2_0', 'WinRing0_1_3_0', 'WinRing0x64')) {
    $chave = Get-ItemProperty -Path ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $nome) `
        -ErrorAction SilentlyContinue
    if ($null -ne $chave) {
        Diga ('Serviço ' + $nome + ': Start=' + $chave.Start + ' ImagePath=' + $chave.ImagePath)
    }
}

$pastas = @(
    "$env:ProgramFiles\KInfo",
    "${env:ProgramFiles(x86)}\KInfo",
    $env:TEMP,
    "$env:WINDIR\Temp"
)

foreach ($pasta in $pastas) {
    if (-not (Test-Path $pasta)) {
        Diga ('Pasta ausente: ' + $pasta)
        continue
    }

    $sys = Get-ChildItem -Path $pasta -Filter '*.sys' -ErrorAction SilentlyContinue
    Diga ('Pasta ' + $pasta + ' → ' +
        $(if ($sys) { ($sys | ForEach-Object { $_.Name + ' (' + $_.Length + ' bytes)' }) -join '; ' }
          else { 'nenhum .sys' }))
}

Despejar (Get-ChildItem -Path "$env:ProgramFiles\KInfo" -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTime) '(pasta de instalação não encontrada)'

Secao 'erro.log do app'

$log = Join-Path $env:APPDATA 'KInfo\erro.log'

if (Test-Path $log) {
    Diga (Get-Content $log -Tail 120 -Encoding UTF8 -ErrorAction SilentlyContinue | Out-String)
} else {
    Diga ('(não existe: ' + $log + ')')
}

$destino = Join-Path ([Environment]::GetFolderPath('Desktop')) 'KInfo-coleta.txt'
$linhas -join "`r`n" | Out-File -FilePath $destino -Encoding UTF8

Write-Host ''
Write-Host 'Pronto. Arquivo gravado em:' -ForegroundColor Green
Write-Host $destino
Write-Host ''
Write-Host 'Envie esse arquivo. Ele diz o que o Windows respondeu sobre o driver.'
Write-Host ''

if (-not $elevado) {
    Write-Host 'Aviso: este PowerShell NAO estava como administrador, entao parte da' -ForegroundColor Yellow
    Write-Host 'coleta voltou vazia por permissao. Rode de novo como administrador.' -ForegroundColor Yellow
    Write-Host ''
}
