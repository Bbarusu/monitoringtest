using System.IO;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace MonitorPC.Services;

/// <summary>
/// Por que os sensores de baixo nivel nao estao respondendo, e um relatorio
/// para quem testa o app poder nos dizer o que o Windows recusou.
///
/// Existe porque o aviso antigo dizia sempre a mesma coisa — "abra como
/// administrador" — inclusive para quem ja estava como administrador. Em dois
/// notebooks diferentes esse recado mandou o usuario repetir uma tentativa que
/// acabara de falhar, e escondeu o motivo de verdade.
///
/// O motivo de verdade quase sempre e o mesmo: a temperatura da CPU e o chip da
/// placa-mae so sao legiveis por um driver de kernel (familia WinRing0), esse
/// driver tem uma falha conhecida de 2020 (CVE-2020-14979) que permite subir a
/// SYSTEM, e por isso o Defender o trata como HackTool:Win32/Winring0 e o poe
/// em quarentena assim que ele aparece. Nao e defeito nosso nem da biblioteca:
/// corrigir exigiria assinatura WHQL, que a Microsoft emite para empresa.
///
/// Num PC de desenvolvimento isso costuma estar resolvido por uma exclusao
/// criada no meio do caminho, o que explica funcionar aqui e falhar na maquina
/// de quem recebe o instalador.
/// </summary>
public static class Diagnostico
{
    /// <summary>O processo tem privilegio de administrador.</summary>
    public static bool Elevado { get; } = Conferir();

    /// <summary>
    /// A Integridade de Memoria do Windows esta ligada. Ela impede o
    /// carregamento de drivers de uma lista que inclui a familia usada aqui.
    /// </summary>
    public static bool IntegridadeDeMemoria { get; } =
        LerNumero(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                  "Enabled") == 1;

    /// <summary>
    /// A lista de drivers vulneraveis esta ativa. O WinRing0 esta nela.
    /// Ausente da chave quer dizer "no padrao", que hoje e ligado.
    /// </summary>
    public static bool BloqueioDeDriversVulneraveis { get; } =
        LerNumero(@"SYSTEM\CurrentControlSet\Control\CI\Config",
                  "VulnerableDriverBlocklistEnable") != 0;

    /// <summary>
    /// O servico do driver existe no registro. Se nem existe, o carregamento
    /// nao chegou a comecar; se existe e nao ha leitura, ele foi recusado.
    /// </summary>
    public static string? ServicoDoDriver { get; } = AcharServico();

    /// <summary>
    /// O arquivo .sys que a biblioteca extrai. Sumir daqui e a assinatura da
    /// quarentena: o antivirus apaga o arquivo depois de ele ser escrito.
    /// </summary>
    public static string? ArquivoDoDriver { get; } = AcharSys();

    // ------------------------------------------------------------------

    private static bool Conferir()
    {
        try
        {
            using var identidade = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identidade).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Le um DWORD do registro. Devolve <c>null</c> quando a chave ou o valor
    /// nao existem, para quem chama poder distinguir "desligado" de "nao disse".
    /// </summary>
    private static int? LerNumero(string caminho, string nome)
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(caminho);
            return chave?.GetValue(nome) as int?;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Procura o servico do driver pelo registro, e nao pelo gerenciador de
    /// servicos: ler o registro nao exige pacote novo nem privilegio extra.
    /// Os nomes variam com a versao da biblioteca, entao vale tentar todos.
    /// </summary>
    private static string? AcharServico()
    {
        string[] nomes =
        [
            "WinRing0_1_2_0", "WinRing0_1_3_0", "WinRing0x64",
            "LibreHardwareMonitor", "R0Sophia"
        ];

        foreach (var nome in nomes)
        {
            try
            {
                using var chave = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{nome}");

                if (chave is not null)
                    return $"{nome} (Start={chave.GetValue("Start")}, " +
                           $"ImagePath={chave.GetValue("ImagePath")})";
            }
            catch
            {
                // Uma chave ilegivel nao pode derrubar a busca pelas outras.
            }
        }

        return null;
    }

    /// <summary>Onde a biblioteca pode ter deixado o .sys, e se ele sobreviveu.</summary>
    private static string? AcharSys()
    {
        var pastas = new List<string>();

        try { pastas.Add(AppContext.BaseDirectory); } catch { }
        try { pastas.Add(Path.GetTempPath()); } catch { }
        try { pastas.Add(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.Windows), "Temp")); } catch { }

        foreach (var pasta in pastas)
        {
            try
            {
                var achados = Directory.GetFiles(pasta, "*.sys")
                    .Where(a => Path.GetFileName(a).Contains("ring0", StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileName(a).Contains("hardwaremonitor", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (achados.Count > 0)
                    return string.Join("; ", achados.Select(a =>
                        $"{a} ({new FileInfo(a).Length} bytes)"));
            }
            catch
            {
                // Pasta sem permissao de listagem: segue para a proxima.
            }
        }

        return null;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// O recado para a faixa de aviso do painel, escolhido pelo que foi de fato
    /// encontrado — e nao pelo palpite mais provavel. O usuario nao fica
    /// repetindo uma tentativa que ja falhou.
    /// </summary>
    public static string PorQueFaltamSensores()
    {
        if (!Elevado)
            return "Temperatura de CPU e placa-mãe indisponíveis. O driver que lê " +
                   "esses valores só carrega com privilégio de administrador — pelo " +
                   "atalho instalado isso acontece sozinho.";

        // Elevado e sem leitura: elevar nao era a resposta. Daqui para baixo o
        // texto precisa dizer o que o Windows fez, porque e a unica informacao
        // que leva a algum lugar.

        if (ArquivoDoDriver is null)
            return "Temperatura de CPU e placa-mãe indisponíveis, mesmo como " +
                   "administrador. O arquivo do driver não está no disco: o " +
                   "antivírus quase certamente o pôs em quarentena. O Defender " +
                   "marca esse driver como HackTool:Win32/Winring0 por uma falha " +
                   "real dele, de 2020 — a marcação está tecnicamente correta. " +
                   "Para liberar é preciso restaurar o arquivo e criar uma exclusão " +
                   "na Segurança do Windows. Salve o relatório abaixo para ver o " +
                   "nome e o caminho exatos.";

        if (IntegridadeDeMemoria)
            return "Temperatura de CPU e placa-mãe indisponíveis, mesmo como " +
                   "administrador. A Integridade de Memória está ligada neste " +
                   "Windows, e ela recusa a família de drivers que lê esses " +
                   "valores. Fica em Segurança do Windows → Segurança do " +
                   "dispositivo → Isolamento de núcleo. É uma proteção real do " +
                   "sistema: desligá-la para ver temperaturas é uma troca, não " +
                   "um conserto.";

        if (BloqueioDeDriversVulneraveis)
            return "Temperatura de CPU e placa-mãe indisponíveis, mesmo como " +
                   "administrador. O driver existe no disco, mas a lista de " +
                   "drivers vulneráveis do Windows está ativa e ele está nessa " +
                   "lista, por uma falha real dele, de 2020. Salve o relatório " +
                   "abaixo: ele traz o erro exato que o Windows devolveu.";

        return "Temperatura de CPU e placa-mãe indisponíveis, mesmo como " +
               "administrador. O driver está no disco e não houve bloqueio " +
               "declarado, então o motivo está no erro que o Windows devolveu. " +
               "Salve o relatório abaixo — ele traz esse erro. Vale lembrar que " +
               "parte dos notebooks não expõe esses sensores de jeito nenhum: " +
               "a leitura fica com o controlador do fabricante.";
    }

    /// <summary>Versao curta, para o rodapé do painel.</summary>
    public static string ResumoCurto() => !Elevado
        ? "Sem acesso aos sensores de baixo nível"
        : ArquivoDoDriver is null
            ? "Driver de sensores em quarentena"
            : IntegridadeDeMemoria
                ? "Driver bloqueado pela Integridade de Memória"
                : "Driver de sensores não carregou";

    // ------------------------------------------------------------------

    /// <summary>
    /// Grava tudo que se sabe sobre a falha num arquivo e devolve o caminho.
    ///
    /// O relatorio da propria biblioteca vai no fim, inteiro: e a unica fonte
    /// que diz o erro exato do carregamento, e cortar linhas dele para ficar
    /// bonito seria jogar fora justamente a parte que responde a pergunta.
    /// </summary>
    public static string Salvar(Computer pc)
    {
        var caminho = Path.Combine(AppConfig.Pasta, "KInfo-diagnostico.txt");
        var t = new StringBuilder();

        t.AppendLine("KInfo — relatório de diagnóstico dos sensores");
        t.AppendLine($"Gerado em {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        t.AppendLine();
        t.AppendLine("Este arquivo é para ser anexado num relato de problema. Ele não");
        t.AppendLine("contém nada de pessoal: só o que o Windows respondeu sobre o");
        t.AppendLine("driver de sensores e quais peças a biblioteca conseguiu ver.");
        t.AppendLine();

        t.AppendLine("--- veredito ---");
        t.AppendLine(PorQueFaltamSensores());
        t.AppendLine();

        t.AppendLine("--- ambiente ---");
        t.AppendLine($"Windows            : {Environment.OSVersion.VersionString}");
        t.AppendLine($"64 bits            : {Environment.Is64BitOperatingSystem}");
        t.AppendLine($"Processo 64 bits   : {Environment.Is64BitProcess}");
        t.AppendLine($"Administrador      : {Elevado}");
        t.AppendLine($"Pasta do app       : {AppContext.BaseDirectory}");
        t.AppendLine();

        t.AppendLine("--- o que pode barrar o driver ---");
        t.AppendLine($"Integridade de Memória (HVCI) : {IntegridadeDeMemoria}");
        t.AppendLine($"Lista de drivers vulneráveis  : {BloqueioDeDriversVulneraveis}");
        t.AppendLine($"Serviço do driver             : {ServicoDoDriver ?? "não existe no registro"}");
        t.AppendLine($"Arquivo .sys                  : {ArquivoDoDriver ?? "não encontrado (sinal de quarentena)"}");
        t.AppendLine();

        t.AppendLine("--- peças que a biblioteca enxergou ---");

        try
        {
            foreach (var h in pc.Hardware)
                t.AppendLine($"{h.HardwareType,-12} {h.Name}  " +
                             $"[{h.Sensors.Count(s => s.Value.HasValue)} leituras]");

            if (!pc.Hardware.Any(h => h.HardwareType == HardwareType.Motherboard))
                t.AppendLine("(nenhuma placa-mãe: é o que acontece sem o driver)");
        }
        catch (Exception ex)
        {
            t.AppendLine($"(falhou ao listar: {ex.Message})");
        }

        t.AppendLine();
        t.AppendLine("--- relatório da LibreHardwareMonitorLib ---");
        t.AppendLine("É aqui que aparece o erro exato do carregamento do driver.");
        t.AppendLine();

        try
        {
            t.AppendLine(pc.GetReport());
        }
        catch (Exception ex)
        {
            t.AppendLine($"(falhou ao gerar: {ex})");
        }

        t.AppendLine();
        t.AppendLine("--- erro.log do app ---");

        try
        {
            t.AppendLine(File.Exists(Log.Caminho)
                ? File.ReadAllText(Log.Caminho)
                : "(vazio: nenhum erro registrado)");
        }
        catch (Exception ex)
        {
            t.AppendLine($"(falhou ao ler: {ex.Message})");
        }

        Directory.CreateDirectory(AppConfig.Pasta);
        File.WriteAllText(caminho, t.ToString());

        return caminho;
    }

    /// <summary>
    /// Abre a pasta com o relatorio ja selecionado.
    ///
    /// Selecionar no Explorer em vez de abrir o txt porque o usuario precisa
    /// anexar o arquivo em algum lugar, nao so le-lo. Fica aqui, e nao na tela,
    /// porque o painel e o menu da bandeja chamam os dois.
    /// </summary>
    public static void Revelar(string arquivo)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{arquivo}\"",
            UseShellExecute = true
        });
    }
}
