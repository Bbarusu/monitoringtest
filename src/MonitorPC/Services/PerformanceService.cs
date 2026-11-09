using System.Diagnostics;
using System.IO;
using MonitorPC.Models;

namespace MonitorPC.Services;

public static class PerformanceService
{
    private static readonly HashSet<string> Protegidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "dwm", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker",
        "shellexperiencehost", "startmenuexperiencehost", "searchhost", "textinputhost",
        "systemsettings", "applicationframehost", "audiodg", "spoolsv", "svchost",
        "system", "idle", "registry", "memory compression", "lsaiso", "wudfhost",

        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray",
        "mpdefendercoreservice", "avp", "avgui", "avastui", "avastsvc", "bdagent",
        "vsserv", "mcshield", "mbamservice", "mbam", "ekrn", "egui", "nortonsecurity",
        "ns", "avguard", "avgsvc", "avgui", "sophosui", "savservice",

        "monitorpc"
    };

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

    public static bool EhProtegido(string nome, int pid) =>
        pid == Environment.ProcessId || Protegidos.Contains(nome);

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
                MemoriaMb = g.Sum(x => x.MemoriaMb),
                TemJanela = g.Any(x => x.TemJanela)
            })
            .OrderByDescending(x => x.MemoriaMb)
            .Take(quantos)
            .ToList();
    }

    private static readonly Dictionary<int, (TimeSpan Tempo, DateTime Quando, DateTime Inicio)>
        AmostraDeCpu = new();

    public static List<ProcessoCpu> PorCpu(int quantos = 12)
    {
        var agora = DateTime.UtcNow;
        var sessao = Process.GetCurrentProcess().SessionId;
        var nucleos = Math.Max(1, Environment.ProcessorCount);

        var lista = new List<ProcessoCpu>();
        var vistos = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != sessao) continue;

                var tempo = p.TotalProcessorTime;
                var inicio = p.StartTime;

                vistos.Add(p.Id);

                double pct = 0;

                if (AmostraDeCpu.TryGetValue(p.Id, out var anterior) && anterior.Inicio == inicio)
                {
                    var decorrido = (agora - anterior.Quando).TotalSeconds;

                    if (decorrido > 0.05)
                        pct = (tempo - anterior.Tempo).TotalSeconds / decorrido / nucleos * 100;
                }

                AmostraDeCpu[p.Id] = (tempo, agora, inicio);

                lista.Add(new ProcessoCpu(
                    p.Id,
                    p.ProcessName,
                    string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle,
                    Math.Clamp(pct, 0, 100),
                    Protegidos.Contains(p.ProcessName) || p.Id == Environment.ProcessId,
                    p.MainWindowHandle != IntPtr.Zero));
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        foreach (var morto in AmostraDeCpu.Keys.Where(k => !vistos.Contains(k)).ToList())
            AmostraDeCpu.Remove(morto);

        return lista
            .GroupBy(x => x.Nome)
            .Select(g => g.OrderByDescending(x => x.CpuPct).First() with
            {
                CpuPct = Math.Clamp(g.Sum(x => x.CpuPct), 0, 100),
                TemJanela = g.Any(x => x.TemJanela)
            })
            .OrderByDescending(x => x.CpuPct)
            .Take(quantos)
            .ToList();
    }

    public sealed record ResultadoDeFechamento(int Pedidos, int SemJanela, int Recusados)
    {
        public int Total => Pedidos + SemJanela + Recusados;
    }

    public static ResultadoDeFechamento FecharPorNome(string nome)
    {
        if (Protegidos.Contains(nome)) return new ResultadoDeFechamento(0, 0, 0);

        int pedidos = 0, semJanela = 0, recusados = 0;

        foreach (var p in Process.GetProcessesByName(nome))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;

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

    public static bool ReiniciarExplorer()
    {
        try
        {
            var antes = Process.GetProcessesByName("explorer");

            foreach (var p in antes)
            {
                try { p.Kill(); } catch {  }
                finally { p.Dispose(); }
            }

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

    public static (int Arquivos, double Mb) MedirTemporarios() => VarrerTemporarios(false);

    public static (int Arquivos, double Mb) LimparTemporarios() => VarrerTemporarios(true);

    private static (int Arquivos, double Mb) VarrerTemporarios(bool apagar)
    {
        var arquivos = 0;
        var bytes = 0L;

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
