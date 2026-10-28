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
    /// Se este processo e um dos que nunca podem ser fechados pelo app, ou o
    /// proprio app. As telas usam isto para nao oferecer um botao que o
    /// <see cref="Fechar"/> recusaria em silencio.
    /// </summary>
    public static bool EhProtegido(string nome, int pid) =>
        pid == Environment.ProcessId || Protegidos.Contains(nome);

    /// <summary>
    /// Pede para o processo fechar pela janela, como se o usuario clicasse no X,
    /// para ele poder salvar o que estiver aberto. Nada e forcado.
    /// </summary>
    public static (int Fechados, double MemoriaMb, int SemJanela) Fechar(IEnumerable<int> pids)
    {
        var fechados = 0;
        var memoria = 0.0;
        var semJanela = 0;

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (Protegidos.Contains(p.ProcessName)) continue;

                var mb = p.WorkingSet64 / 1048576.0;

                // Sem janela principal nao ha para onde mandar o pedido.
                // Contado a parte porque leva a outro caminho: quem chama pode
                // oferecer forcar, que e a unica saida nesse caso.
                if (p.MainWindowHandle == IntPtr.Zero)
                {
                    semJanela++;
                    continue;
                }

                if (!p.CloseMainWindow()) continue;

                fechados++;
                memoria += mb;
            }
            catch (Exception ex)
            {
                Log.Erro("Fechar processo", ex);
            }
        }

        return (fechados, memoria, semJanela);
    }

    /// <summary>
    /// Encerra a forca os processos destes PIDs. Mesmas ressalvas de
    /// <see cref="ForcarPorNome"/>: nada e perguntado ao programa, e quem
    /// chama precisa ter perguntado ao usuario.
    /// </summary>
    public static int Forcar(IEnumerable<int> pids)
    {
        var encerrados = 0;

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (Protegidos.Contains(p.ProcessName)) continue;
                if (p.Id == Environment.ProcessId) continue;

                p.Kill();
                p.WaitForExit(2000);
                encerrados++;
            }
            catch (Exception ex)
            {
                Log.Erro("Forçar encerramento", ex);
            }
        }

        return encerrados;
    }

    /// <summary>
    /// Todos os processos da sessao por consumo de memoria, inclusive os
    /// protegidos — que aparecem marcados, para a lista nao parecer incompleta.
    /// </summary>
    public static List<ProcessoMemoria> PorMemoria(int quantos = 12)
    {
        var sessao = Process.GetCurrentProcess().SessionId;
        var lista = new List<ProcessoMemoria>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != sessao) continue;

                lista.Add(new ProcessoMemoria(
                    p.Id,
                    p.ProcessName,
                    string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle,
                    p.WorkingSet64 / 1048576.0,
                    Protegidos.Contains(p.ProcessName) || p.Id == Environment.ProcessId,
                    p.MainWindowHandle != IntPtr.Zero));
            }
            catch
            {
                // Processo encerrado no meio da varredura.
            }
            finally
            {
                p.Dispose();
            }
        }

        // Varios processos com o mesmo nome (abas de navegador, por exemplo)
        // viram uma linha so, com a soma.
        return lista
            .GroupBy(x => x.Nome)
            .Select(g => g.OrderByDescending(x => x.MemoriaMb).First() with
            {
                MemoriaMb = g.Sum(x => x.MemoriaMb),
                TemJanela = g.Any(x => x.TemJanela)
            })
            .OrderByDescending(x => x.MemoriaMb)
            .Take(quantos)
            .ToList();
    }

    /// <summary>
    /// O que aconteceu ao pedir para um programa fechar.
    ///
    /// <paramref name="SemJanela"/> existe separado de <paramref name="Recusados"/>
    /// porque sao coisas diferentes e levam a caminhos diferentes: quem recusou
    /// ouviu o pedido e pode estar perguntando algo ao usuario; quem nao tem
    /// janela nunca vai ouvir pedido nenhum, e para esse so forcar resolve.
    /// </summary>
    public sealed record ResultadoDeFechamento(int Pedidos, int SemJanela, int Recusados)
    {
        public int Total => Pedidos + SemJanela + Recusados;
    }

    /// <summary>
    /// Pede para os processos com este nome fecharem, pelo mesmo caminho do
    /// botao X da janela deles. Nada e forcado aqui.
    /// </summary>
    public static ResultadoDeFechamento FecharPorNome(string nome)
    {
        if (Protegidos.Contains(nome)) return new ResultadoDeFechamento(0, 0, 0);

        int pedidos = 0, semJanela = 0, recusados = 0;

        foreach (var p in Process.GetProcessesByName(nome))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;

                // Sem janela principal nao ha para onde mandar o pedido de
                // fechar. CloseMainWindow devolveria false na hora, e era isso
                // que fazia o botao nao surtir efeito nenhum em programa de
                // segundo plano — sem fechar e sem explicar o porque.
                if (p.MainWindowHandle == IntPtr.Zero)
                {
                    semJanela++;
                    continue;
                }

                if (p.CloseMainWindow()) pedidos++;
                else recusados++;
            }
            catch (Exception ex)
            {
                Log.Erro("Fechar por nome", ex);
            }
            finally
            {
                p.Dispose();
            }
        }

        return new ResultadoDeFechamento(pedidos, semJanela, recusados);
    }

    /// <summary>
    /// Encerra a forca os processos com este nome. Devolve quantos cairam.
    ///
    /// Isto nao pergunta nada ao programa: ele para onde estiver, e o que nao
    /// tiver sido salvo se perde. E o unico caminho possivel para quem nao tem
    /// janela, e por isso existe — mas quem chama precisa ter perguntado ao
    /// usuario antes, porque daqui nao ha como desfazer.
    ///
    /// A lista de protegidos vale aqui tambem, e com mais razao.
    /// </summary>
    public static int ForcarPorNome(string nome)
    {
        if (Protegidos.Contains(nome)) return 0;

        var encerrados = 0;

        foreach (var p in Process.GetProcessesByName(nome))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;

                p.Kill();

                // Espera curta: confirma que caiu mesmo, sem segurar a tela se
                // o processo demorar a sair.
                p.WaitForExit(2000);
                encerrados++;
            }
            catch (Exception ex)
            {
                Log.Erro("Forçar encerramento", ex);
            }
            finally
            {
                p.Dispose();
            }
        }

        return encerrados;
    }

    // ------------------------------------------------------------------
    // Explorer

    /// <summary>
    /// Reinicia o Explorer: encerra e sobe de novo. Util quando a barra de
    /// tarefas trava ou a memoria dele cresce demais.
    ///
    /// O nosso app e outro processo, entao nao cai junto. O que some e a area de
    /// notificacao, recriada pelo Explorer; quem chama precisa reinstalar o
    /// icone da bandeja depois.
    /// </summary>
    public static bool ReiniciarExplorer()
    {
        try
        {
            var antes = Process.GetProcessesByName("explorer");

            foreach (var p in antes)
            {
                try { p.Kill(); } catch { /* pode ja ter saido */ }
                finally { p.Dispose(); }
            }

            // O Windows costuma subir o shell sozinho (AutoRestartShell). Da um
            // tempo antes de decidir, senao sobem duas instancias.
            Thread.Sleep(1500);

            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });

            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Reiniciar Explorer", ex);
            return false;
        }
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
