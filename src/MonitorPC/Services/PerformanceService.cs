using System.Diagnostics;
using System.IO;
using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>
/// As acoes do Modo Desempenho. Cada uma devolve o que conseguiu fazer, para o
/// painel relatar o resultado real em vez de prometer o que nao aconteceu.
/// </summary>
public static class PerformanceService
{
    /// <summary>
    /// Processos que nunca entram na lista de fechaveis, qualquer que seja o
    /// consumo: o PC fica inutilizavel ou desprotegido sem eles.
    /// </summary>
    private static readonly HashSet<string> Protegidos = new(StringComparer.OrdinalIgnoreCase)
    {
        // Essenciais do Windows
        "explorer", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "dwm", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "textinputhost",
        "systemsettings", "applicationframehost", "audiodg", "spoolsv", "svchost",
        "system", "idle", "registry", "memory compression", "lsaiso", "wudfhost",

        // Seguranca
        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray",
        "mpdefendercoreservice", "avp", "avgui", "avastui", "avastsvc", "bdagent",
        "vsserv", "mcshield", "mbamservice", "mbam", "ekrn", "egui", "nortonsecurity",
        "ns", "avguard", "avgsvc", "avgui", "sophosui", "savservice",

        // O proprio monitor
        "monitorpc"
    };

    // ------------------------------------------------------------------
    // Processos

    /// <summary>
    /// Processos que o usuario pode fechar, do maior consumo de memoria para o
    /// menor. So entram os que tem janela propria e rodam na mesma sessao: um
    /// servico em segundo plano nao e coisa de fechar na mao.
    /// </summary>
    public static List<ProcessoFechavel> Fechaveis(int quantos = 8)
    {
        var sessao = Process.GetCurrentProcess().SessionId;
        var eu = Environment.ProcessId;
        var lista = new List<ProcessoFechavel>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == eu || p.SessionId != sessao) continue;
                if (Protegidos.Contains(p.ProcessName)) continue;
                if (p.MainWindowHandle == IntPtr.Zero) continue;

                lista.Add(new ProcessoFechavel(
                    p.Id,
                    p.ProcessName,
                    string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle,
                    p.WorkingSet64 / 1048576.0));
            }
            catch
            {
                // Processos que somem ou negam acesso no meio da varredura.
            }
            finally
            {
                p.Dispose();
            }
        }

        return lista
            .GroupBy(x => x.Nome)
            .Select(g => g.OrderByDescending(x => x.MemoriaMb).First() with
            {
                MemoriaMb = g.Sum(x => x.MemoriaMb)
            })
            .OrderByDescending(x => x.MemoriaMb)
            .Take(quantos)
            .ToList();
    }

    /// <summary>
    /// Pede para o processo fechar pela janela, como se o usuario clicasse no X,
    /// para ele poder salvar o que estiver aberto. Nada e forcado.
    /// </summary>
    public static (int Fechados, double MemoriaMb) Fechar(IEnumerable<int> pids)
    {
        var fechados = 0;
        var memoria = 0.0;

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (Protegidos.Contains(p.ProcessName)) continue;

                var mb = p.WorkingSet64 / 1048576.0;

                if (!p.CloseMainWindow()) continue;

                fechados++;
                memoria += mb;
            }
            catch (Exception ex)
            {
                Log.Erro("Fechar processo", ex);
            }
        }

        return (fechados, memoria);
    }

    // ------------------------------------------------------------------
    // Arquivos temporarios

    /// <summary>Quanto ha para limpar, sem apagar nada.</summary>
    public static (int Arquivos, double Mb) MedirTemporarios() => VarrerTemporarios(false);

    /// <summary>Apaga o que der; o que estiver em uso fica.</summary>
    public static (int Arquivos, double Mb) LimparTemporarios() => VarrerTemporarios(true);

    private static (int Arquivos, double Mb) VarrerTemporarios(bool apagar)
    {
        var arquivos = 0;
        var bytes = 0L;

        // Arquivos recem-criados podem estar em uso por um programa aberto.
        var corte = DateTime.Now.AddHours(-1);

        foreach (var pasta in new[] { Path.GetTempPath() })
        {
            if (!Directory.Exists(pasta)) continue;

            foreach (var caminho in Seguro(pasta))
            {
                try
                {
                    var info = new FileInfo(caminho);
                    if (info.LastWriteTime > corte) continue;

                    var tamanho = info.Length;
                    if (apagar) info.Delete();

                    arquivos++;
                    bytes += tamanho;
                }
                catch
                {
                    // Em uso, sem permissao ou ja removido: segue para o proximo.
                }
            }
        }

        return (arquivos, bytes / 1048576.0);
    }

    private static IEnumerable<string> Seguro(string pasta)
    {
        try
        {
            return Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories);
        }
        catch
        {
            try { return Directory.EnumerateFiles(pasta); }
            catch { return Array.Empty<string>(); }
        }
    }
}
