using System.IO;
using System.Runtime.InteropServices;
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
/// O motivo de verdade, medido em tres maquinas, e a falta do PawnIO. A
/// temperatura da CPU e o chip da placa-mae so sao legiveis por um driver de
/// kernel, e a LibreHardwareMonitorLib 0.9.6 nao embute mais driver nenhum:
/// ela abandonou a familia WinRing0 — que tem falha conhecida de 2020
/// (CVE-2020-14979) e entrou na lista de drivers bloqueados do Windows — e
/// passou a depender do PawnIO, instalado no sistema por fora.
///
/// Sem o PawnIO a biblioteca nao reclama: devolve a lista de sensores vazia e
/// segue. Por isso a falha nao deixava rastro nenhum — nenhum arquivo escrito,
/// nenhum servico criado, nenhuma recusa no log do Windows, nenhuma deteccao
/// do antivirus. Nao havia nada sendo instalado para dar errado.
///
/// Num PC de desenvolvimento o PawnIO costuma ja estar la, posto pelo
/// LibreHardwareMonitor oficial em algum momento. E o que explica funcionar
/// aqui e falhar na maquina de quem recebe o instalador.
///
/// A ordem das conferencias abaixo segue essa descoberta: PawnIO primeiro,
/// porque sem ele nao ha leitura nem com privilegio de administrador.
/// </summary>
public static class Diagnostico
{
    /// <summary>O processo tem privilegio de administrador.</summary>
    public static bool Elevado { get; } = Conferir();

    /// <summary>
    /// O PawnIO esta instalado nesta maquina.
    ///
    /// E a dependencia de verdade, e ela nao viaja com o app. A
    /// LibreHardwareMonitorLib 0.9.6 nao embute mais driver nenhum: ela
    /// abandonou a familia WinRing0 — que tem falha conhecida de 2020 e entrou
    /// na lista de drivers bloqueados do Windows — e passou a usar o PawnIO,
    /// que e um driver assinado, instalado no sistema por fora, uma vez.
    ///
    /// Sem ele a biblioteca nao reclama: devolve a lista de sensores vazia e
    /// segue. Foi o que escondeu a causa por uma conversa inteira — nao havia
    /// arquivo sendo escrito, nem servico sendo criado, nem recusa do Windows,
    /// porque nao havia nada sendo instalado.
    ///
    /// Numa maquina de desenvolvimento ele costuma ja estar la, instalado pelo
    /// LibreHardwareMonitor oficial em algum momento. E por isso que funcionava
    /// aqui e falhava em maquina limpa.
    /// </summary>
    /// <remarks>
    /// Propriedade calculada, e nao campo: um campo declarado antes de
    /// <see cref="DetalheDoPawnIo"/> leria nulo na inicializacao estatica.
    /// </remarks>
    public static bool PawnIo => DetalheDoPawnIo is not null;

    /// <summary>O que foi encontrado do PawnIO, para o relatorio.</summary>
    public static string? DetalheDoPawnIo { get; } = AcharPawnIo();

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

    /// <summary>
    /// Procura o PawnIO pelo servico no registro e pelo arquivo em disco.
    /// Devolve o que achou, ou <c>null</c> quando nao esta instalado.
    ///
    /// Duas vias porque sao perguntas diferentes: o servico diz que o Windows
    /// conhece o driver, o arquivo diz que ele esta no disco. Instalacao pela
    /// metade — servico sem arquivo, por exemplo — aparece no relatorio em vez
    /// de virar um "nao instalado" que mandaria instalar de novo.
    /// </summary>
    /// <summary>
    /// O estado real do servico do PawnIO: rodando, parado, ou nem registrado.
    ///
    /// Existe porque <see cref="PawnIo"/> respondia "instalado" olhando so a
    /// chave do registro — presenca, nao funcionamento. Numa maquina em que a
    /// leitura inteira de MSR parou depois de um reinicio, o relatorio dizia
    /// "PawnIO Instalado: True" e mandava procurar noutro lugar, quando o
    /// problema era o driver registrado e nao carregado.
    ///
    /// E o mesmo erro que o aviso do painel cometia antes: conferir que a coisa
    /// existe e concluir que ela funciona.
    /// </summary>
    /// <remarks>
    /// Consultado a cada leitura, e nao guardado: o app sobe o driver quando
    /// ele esta parado, e um valor de antes dessa tentativa faria o relatorio
    /// descrever um estado que ja nao existe.
    /// </remarks>
    public static string EstadoDoPawnIo => ConsultarEstadoDoPawnIo();

    private const int AcessoDeConsulta = 0x0004;      // SERVICE_QUERY_STATUS
    private const int AcessoParaIniciar = 0x0014;     // SERVICE_START | SERVICE_QUERY_STATUS
    private const int AcessoAoGerenciador = 0x0001;   // SC_MANAGER_CONNECT

    /// <summary>
    /// Sobe o PawnIO quando ele esta registrado e parado. Devolve o que
    /// aconteceu, para quem chama registrar.
    ///
    /// Existe porque o PawnIO e instalado com inicio sob demanda: ele fica
    /// registrado e parado ate alguem pedir para carregar. A biblioteca de
    /// sensores nao pede — ela tenta usar e falha calada —, e por isso a
    /// leitura funcionava logo depois de instalar o PawnIO, que o deixa
    /// rodando, e morria no primeiro reinicio, quando ele voltava parado.
    /// Sem isto, toda maquina perde os sensores no primeiro reboot.
    ///
    /// Subir o driver e acao de sistema, e e deliberada: foi o nosso
    /// instalador que o instalou, para este fim, e o app roda elevado
    /// justamente para falar com ele. Nada e instalado nem configurado aqui —
    /// so e carregado o que ja estava registrado.
    /// </summary>
    public static string GarantirPawnIo()
    {
        var gerenciador = IntPtr.Zero;
        var servico = IntPtr.Zero;

        try
        {
            gerenciador = OpenSCManager(null, null, AcessoAoGerenciador);
            if (gerenciador == IntPtr.Zero)
                return "não foi possível falar com o gerenciador de serviços";

            servico = OpenService(gerenciador, "PawnIO", AcessoParaIniciar);
            if (servico == IntPtr.Zero)
                return "PawnIO não está registrado neste Windows";

            var estado = new EstadoDeServico();
            if (QueryServiceStatus(servico, ref estado) && estado.EstadoAtual == 4)
                return "já estava rodando";

            if (StartService(servico, 0, null))
                return "iniciado pelo app";

            var erro = Marshal.GetLastWin32Error();

            // 1056 é ERROR_SERVICE_ALREADY_RUNNING: outro processo subiu o
            // driver entre a conferência e a tentativa. Não é falha.
            if (erro == 1056) return "já estava rodando";

            // 5 é acesso negado, e nesse caso a causa é elevação, não o driver.
            return erro == 5
                ? "sem privilégio para iniciar o PawnIO"
                : $"não foi possível iniciar o PawnIO: erro {erro} do Windows";
        }
        catch (Exception ex)
        {
            Log.Erro("Iniciar PawnIO", ex);
            return "falha ao tentar iniciar o PawnIO";
        }
        finally
        {
            if (servico != IntPtr.Zero) CloseServiceHandle(servico);
            if (gerenciador != IntPtr.Zero) CloseServiceHandle(gerenciador);
        }
    }

    private static string ConsultarEstadoDoPawnIo()
    {
        var gerenciador = IntPtr.Zero;
        var servico = IntPtr.Zero;

        try
        {
            gerenciador = OpenSCManager(null, null, AcessoAoGerenciador);
            if (gerenciador == IntPtr.Zero) return "não foi possível consultar o gerenciador de serviços";

            servico = OpenService(gerenciador, "PawnIO", AcessoDeConsulta);
            if (servico == IntPtr.Zero) return "não registrado";

            var estado = new EstadoDeServico();
            if (!QueryServiceStatus(servico, ref estado)) return "registrado, estado ilegível";

            return estado.EstadoAtual switch
            {
                1 => "PARADO — registrado, mas não carregado",
                2 => "iniciando",
                3 => "parando",
                4 => "rodando",
                5 => "continuando",
                6 => "pausando",
                7 => "pausado",
                _ => $"desconhecido ({estado.EstadoAtual})"
            };
        }
        catch (Exception ex)
        {
            Log.Erro("Consultar PawnIO", ex);
            return "falha ao consultar";
        }
        finally
        {
            if (servico != IntPtr.Zero) CloseServiceHandle(servico);
            if (gerenciador != IntPtr.Zero) CloseServiceHandle(gerenciador);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EstadoDeServico
    {
        public int Tipo;
        public int EstadoAtual;
        public int ControlesAceitos;
        public int CodigoDeSaida;
        public int CodigoEspecifico;
        public int PontoDeVerificacao;
        public int DicaDeEspera;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? maquina, string? base_, int acesso);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr gerenciador, string nome, int acesso);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr servico, ref EstadoDeServico estado);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr servico, int quantosArgumentos, string[]? argumentos);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static string? AcharPawnIo()
    {
        var achados = new List<string>();

        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\PawnIO");

            if (chave is not null)
                achados.Add($"serviço PawnIO (Start={chave.GetValue("Start")}, " +
                            $"ImagePath={chave.GetValue("ImagePath")})");
        }
        catch
        {
            // Chave ilegivel nao pode impedir a busca pelo arquivo.
        }

        foreach (var pasta in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            try
            {
                var dir = Path.Combine(pasta, "PawnIO");
                if (!Directory.Exists(dir)) continue;

                var arquivos = Directory.GetFiles(dir, "PawnIO*");
                achados.Add(arquivos.Length > 0
                    ? $"{dir} ({arquivos.Length} {(arquivos.Length == 1 ? "arquivo" : "arquivos")})"
                    : $"{dir} (pasta vazia)");
            }
            catch
            {
                // Pasta sem permissao de listagem: segue.
            }
        }

        return achados.Count > 0 ? string.Join("; ", achados) : null;
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
        // O PawnIO vem primeiro porque e a causa real e a mais comum: sem ele
        // nao ha leitura nem com privilegio de administrador, e mandar elevar
        // seria mandar repetir o que nao resolve.
        if (!PawnIo)
            return "Temperatura de CPU e placa-mãe indisponíveis: falta o " +
                   "PawnIO neste Windows. É um driver assinado, instalado uma " +
                   "vez, que dá ao app acesso aos sensores de baixo nível — a " +
                   "biblioteca que o KInfo usa depende dele e não o traz " +
                   "consigo. Instale em pawnio.eu e reabra o app. Enquanto " +
                   "isso, placa de vídeo, discos, memória e uso de CPU " +
                   "continuam funcionando normalmente.";

        // Instalado nao e o mesmo que carregado, e a diferenca entre os dois
        // ja mandou uma investigacao para o lado errado: o relatorio dizia
        // "PawnIO Instalado: True" numa maquina onde a leitura de MSR tinha
        // parado por completo depois de um reinicio.
        // Chegar aqui com o serviço parado significa que a tentativa de subir
        // falhou, porque o app pede isso a cada abertura. Então o recado não é
        // mais "reinicie o PC": é dizer o que impediu.
        if (EstadoDoPawnIo.StartsWith("PARADO", StringComparison.Ordinal))
            return Elevado
                ? "Temperatura de CPU e placa-mãe indisponíveis: o PawnIO está " +
                  "instalado, o app tentou carregá-lo e o Windows recusou. O erro " +
                  "exato ficou no erro.log. Reinstalar o PawnIO em pawnio.eu " +
                  "costuma resolver. Placa de vídeo, discos, memória e uso de CPU " +
                  "não dependem dele e continuam normais."
                : "Temperatura de CPU e placa-mãe indisponíveis: o PawnIO está " +
                  "instalado mas parado, e carregá-lo exige privilégio de " +
                  "administrador, que este app não tem agora. Pelo atalho " +
                  "instalado isso acontece sozinho.";

        if (EstadoDoPawnIo == "não registrado")
            return "Temperatura de CPU e placa-mãe indisponíveis: o serviço do " +
                   "PawnIO não está registrado neste Windows, mesmo havendo " +
                   "arquivos dele no disco. Reinstale em pawnio.eu e reabra o app.";

        if (!Elevado)
            return "Temperatura de CPU e placa-mãe indisponíveis. O driver que lê " +
                   "esses valores só carrega com privilégio de administrador — pelo " +
                   "atalho instalado isso acontece sozinho.";

        // Elevado e sem leitura: elevar nao era a resposta. Daqui para baixo o
        // texto precisa dizer o que o Windows fez, porque e a unica informacao
        // que leva a algum lugar.

        // Aqui havia tres recados sobre quarentena do Defender, Integridade de
        // Memoria e lista de drivers vulneraveis. Os tres descreviam o
        // WinRing0, que esta biblioteca nao usa mais, e os tres foram testados
        // em tres maquinas: nenhum era a causa. O PawnIO e assinado, e um
        // bloqueio dele deixaria rastro no log CodeIntegrity do Windows.
        // Repetir palpite num aviso que o usuario le como diagnostico foi o
        // erro que custou mais tempo neste projeto.
        return "Temperatura de CPU e placa-mãe indisponíveis, mesmo com o " +
               "PawnIO instalado e privilégio de administrador. Duas " +
               "possibilidades: o PawnIO acabou de ser instalado e o serviço " +
               "ainda não subiu, e aí reiniciar resolve; ou esta placa não " +
               "expõe esses sensores à biblioteca, o que acontece em parte dos " +
               "notebooks, onde a leitura fica com o controlador do fabricante. " +
               "Salve o relatório abaixo: ele traz o relatório interno da " +
               "biblioteca, que diz qual dos dois é.";
    }

    /// <summary>Versao curta, para o rodapé do painel.</summary>
    public static string ResumoCurto() => !PawnIo
        ? "PawnIO não instalado · sensores de baixo nível indisponíveis"
        : !Elevado
            ? "Sem acesso aos sensores de baixo nível"
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

        // O veredito era impresso sempre, e em maquina que le normalmente o
        // relatorio afirmava que a temperatura estava indisponivel com 59 °C de
        // CPU listados tres linhas abaixo. Um relatorio de diagnostico que
        // contradiz os proprios dados nao serve para nada.
        t.AppendLine(TemTemperaturaDeCpu(pc)
            ? "Os sensores de baixo nível estão respondendo: há temperatura de CPU " +
              "nesta leitura. Se você veio parar aqui por outro motivo, o problema " +
              "não é o driver."
            : PorQueFaltamSensores());

        t.AppendLine();

        t.AppendLine("--- ambiente ---");

        // O commit vai junto. Numa rodada de testes em maquinas de outras
        // pessoas, "0.1.0" nao distingue duas builds do mesmo dia, e sem isso
        // um relato chega sem dizer qual codigo estava rodando.
        t.AppendLine($"KInfo              : {Comum.Versao.Completa}");
        t.AppendLine($"Windows            : {Environment.OSVersion.VersionString}");
        t.AppendLine($"64 bits            : {Environment.Is64BitOperatingSystem}");
        t.AppendLine($"Processo 64 bits   : {Environment.Is64BitProcess}");
        t.AppendLine($"Administrador      : {Elevado}");
        t.AppendLine($"Pasta do app       : {AppContext.BaseDirectory}");
        t.AppendLine();

        t.AppendLine("--- PawnIO (a dependência que importa) ---");
        t.AppendLine($"Instalado : {PawnIo}");

        // O estado vem antes de "Onde" porque e ele que decide. Instalado e
        // parado le tanto quanto nao instalado, e esta linha e a diferenca
        // entre procurar o problema no lugar certo e no lugar errado.
        t.AppendLine($"Serviço   : {EstadoDoPawnIo}");
        t.AppendLine($"Onde      : {DetalheDoPawnIo ?? "não encontrado"}");
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
            // Desce no sub-hardware. A placa-mae nao tem sensor proprio: quem
            // tem e o chip de monitoramento pendurado nela, e sem descer a
            // listagem dizia "Motherboard [0 leituras]" numa maquina onde o
            // Nuvoton estava entregando temperaturas, tensoes e rotacoes. O
            // relatorio acusava uma falha que nao existia.
            void Listar(IHardware h, int nivel)
            {
                var recuo = new string(' ', nivel * 2);

                t.AppendLine($"{recuo}{h.HardwareType,-12} {h.Name}  " +
                             $"[{h.Sensors.Count(s => s.Value.HasValue)} leituras]");

                foreach (var sub in h.SubHardware) Listar(sub, nivel + 1);
            }

            foreach (var h in pc.Hardware) Listar(h, 0);

            if (!pc.Hardware.Any(h => h.HardwareType == HardwareType.Motherboard))
                t.AppendLine("(nenhuma placa-mãe na lista)");
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
    /// Ha temperatura de CPU nesta leitura — o sinal de que o acesso de baixo
    /// nivel esta funcionando. Desce no sub-hardware porque e onde moram os
    /// sensores que vem pelo chip da placa-mae.
    /// </summary>
    private static bool TemTemperaturaDeCpu(Computer pc)
    {
        try
        {
            return pc.Hardware.Any(Tem);
        }
        catch
        {
            return false;
        }

        static bool Tem(IHardware h) =>
            (h.HardwareType == HardwareType.Cpu &&
             h.Sensors.Any(s => s.SensorType == SensorType.Temperature && s.Value.HasValue))
            || h.SubHardware.Any(Tem);
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
