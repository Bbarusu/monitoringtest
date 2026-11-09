using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed class DiskProcessService
{
    private readonly Dictionary<int, (ulong Lidos, ulong Escritos)> _anterior = new();

    private readonly Dictionary<int, string?> _caminhos = new();
    private DateTime _ultimaAmostra = DateTime.MinValue;

    private readonly object _trava = new();

    public void Reiniciar()
    {
        lock (_trava)
        {
            _anterior.Clear();
            _ultimaAmostra = DateTime.MinValue;
        }
    }

    public IReadOnlyList<ProcessoDisco> MedirTudo() => Medir(int.MaxValue);

    public IReadOnlyList<ProcessoDisco> Medir(int quantos = 3)
    {
        var atual = new Dictionary<int, (ulong, ulong)>();
        var nomes = new Dictionary<int, string>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!GetProcessIoCounters(p.Handle, out var c)) continue;
                atual[p.Id] = (c.ReadTransferCount, c.WriteTransferCount);
                nomes[p.Id] = p.ProcessName;
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        List<ProcessoDisco> topo;

        lock (_trava)
        {
            var agora = DateTime.UtcNow;
            var dt = (agora - _ultimaAmostra).TotalSeconds;
            _ultimaAmostra = agora;

            if (dt is <= 0 or > 60 || _anterior.Count == 0)
            {
                Guardar(atual);
                return Array.Empty<ProcessoDisco>();
            }

            var lista = new List<ProcessoDisco>();

            foreach (var (pid, (lidos, escritos)) in atual)
            {
                if (!_anterior.TryGetValue(pid, out var antes)) continue;

                if (lidos < antes.Lidos || escritos < antes.Escritos) continue;

                var leitura = (lidos - antes.Lidos) / dt / 1_048_576.0;
                var escrita = (escritos - antes.Escritos) / dt / 1_048_576.0;

                if (leitura + escrita < 0.05) continue;

                lista.Add(new ProcessoDisco(pid, nomes.GetValueOrDefault(pid, "?"),
                                            leitura, escrita));
            }

            Guardar(atual);

            topo = lista.OrderByDescending(x => x.TotalMbs).Take(quantos).ToList();
        }

        return topo.Select(p => p with { Caminho = Caminho(p.Pid) }).ToList();
    }

    private string? Caminho(int pid)
    {
        lock (_trava)
            if (_caminhos.TryGetValue(pid, out var guardado)) return guardado;

        var caminho = Consultar(pid);

        lock (_trava) _caminhos[pid] = caminho;

        return caminho;
    }

    private static string? Consultar(int pid)
    {
        var processo = OpenProcess(ConsultaLimitada, false, pid);
        if (processo == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var tamanho = buffer.Capacity;

            return QueryFullProcessImageName(processo, 0, buffer, ref tamanho)
                ? buffer.ToString()
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(processo);
        }
    }

    private void Guardar(Dictionary<int, (ulong, ulong)> atual)
    {
        _anterior.Clear();
        foreach (var (pid, valores) in atual) _anterior[pid] = valores;

        foreach (var pid in _caminhos.Keys.Where(k => !atual.ContainsKey(k)).ToList())
            _caminhos.Remove(pid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processo, out IoCounters contadores);

    private const int ConsultaLimitada = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int acesso, [MarshalAs(UnmanagedType.Bool)] bool herdar, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processo, int flags, StringBuilder nome, ref int tamanho);
}
