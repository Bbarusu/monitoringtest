using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace MonitorPC.Services;

public static class Diagnostico
{
    public static bool Elevado { get; } = Conferir();

    public static bool PawnIo => DetalheDoPawnIo is not null;

    public static string? DetalheDoPawnIo { get; } = AcharPawnIo();

    public static bool IntegridadeDeMemoria { get; } =
        LerNumero(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
                  "Enabled") == 1;

    public static bool BloqueioDeDriversVulneraveis { get; } =
        LerNumero(@"SYSTEM\CurrentControlSet\Control\CI\Config",
                  "VulnerableDriverBlocklistEnable") != 0;

    public static string? ServicoDoDriver { get; } = AcharServico();

    public static string? ArquivoDoDriver { get; } = AcharSys();

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
            }
        }

        return null;
    }

    public static string EstadoDoPawnIo => ConsultarEstadoDoPawnIo();

    private const int AcessoDeConsulta = 0x0004;
    private const int AcessoParaIniciar = 0x0014;
    private const int AcessoAoGerenciador = 0x0001;

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

            if (erro == 1056) return "já estava rodando";

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
            }
        }

        return achados.Count > 0 ? string.Join("; ", achados) : null;
    }

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
            }
        }

        return null;
    }

    public static string PorQueFaltamSensores()
    {
        if (!PawnIo)
            return "Temperatura de CPU e placa-mãe indisponíveis: falta o " +
                   "PawnIO neste Windows. É um driver assinado, instalado uma " +
                   "vez, que dá ao app acesso aos sensores de baixo nível — a " +
                   "biblioteca que o KInfo usa depende dele e não o traz " +
                   "consigo. Instale em pawnio.eu e reabra o app. Enquanto " +
                   "isso, placa de vídeo, discos, memória e uso de CPU " +
                   "continuam funcionando normalmente.";

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

        return "Temperatura de CPU e placa-mãe indisponíveis, mesmo com o " +
               "PawnIO instalado e privilégio de administrador. Duas " +
               "possibilidades: o PawnIO acabou de ser instalado e o serviço " +
               "ainda não subiu, e aí reiniciar resolve; ou esta placa não " +
               "expõe esses sensores à biblioteca, o que acontece em parte dos " +
               "notebooks, onde a leitura fica com o controlador do fabricante. " +
               "Salve o relatório abaixo: ele traz o relatório interno da " +
               "biblioteca, que diz qual dos dois é.";
    }

    public static string ResumoCurto() => !PawnIo
        ? "PawnIO não instalado · sensores de baixo nível indisponíveis"
        : !Elevado
            ? "Sem acesso aos sensores de baixo nível"
            : IntegridadeDeMemoria
                ? "Driver bloqueado pela Integridade de Memória"
                : "Driver de sensores não carregou";

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

        t.AppendLine(TemTemperaturaDeCpu(pc)
            ? "Os sensores de baixo nível estão respondendo: há temperatura de CPU " +
              "nesta leitura. Se você veio parar aqui por outro motivo, o problema " +
              "não é o driver."
            : PorQueFaltamSensores());

        t.AppendLine();

        t.AppendLine("--- ambiente ---");

        t.AppendLine($"KInfo              : {Comum.Versao.Completa}");
        t.AppendLine($"Windows            : {Environment.OSVersion.VersionString}");
        t.AppendLine($"64 bits            : {Environment.Is64BitOperatingSystem}");
        t.AppendLine($"Processo 64 bits   : {Environment.Is64BitProcess}");
        t.AppendLine($"Administrador      : {Elevado}");
        t.AppendLine($"Pasta do app       : {AppContext.BaseDirectory}");
        t.AppendLine();

        t.AppendLine("--- PawnIO (a dependência que importa) ---");
        t.AppendLine($"Instalado : {PawnIo}");

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
