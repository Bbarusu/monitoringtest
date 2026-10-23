using System.Diagnostics;
using System.Runtime.InteropServices;
using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>
/// Quanto cada processo le e escreve em disco, por diferenca entre duas
/// amostras dos contadores do proprio Windows.
///
/// So roda enquanto a aba Disco esta aberta: varrer a lista de processos a
/// cada segundo o tempo todo contrariaria a meta de nao pesar no sistema.
/// Isto nao e o rastreamento avancado, que ve arquivo por arquivo e tem custo
/// bem maior.
/// </summary>
public sealed class DiskProcessService
{
    private readonly Dictionary<int, (ulong Lidos, ulong Escritos)> _anterior = new();
    private DateTime _ultimaAmostra = DateTime.MinValue;

    /// <summary>Esquece as amostras, para a proxima leitura nao virar um pico falso.</summary>
    public void Reiniciar()
    {
        _anterior.Clear();
        _ultimaAmostra = DateTime.MinValue;
    }

    public IReadOnlyList<ProcessoDisco> Medir(int quantos = 3)
    {
        var agora = DateTime.UtcNow;
        var dt = (agora - _ultimaAmostra).TotalSeconds;
        _ultimaAmostra = agora;

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
                // Processos protegidos do sistema negam acesso ao handle.
                // Nao da para medi-los, e tentar de novo nao ajuda.
            }
            finally
            {
                p.Dispose();
            }
        }

        // A primeira amostra so serve de referencia: sem anterior nao ha taxa.
        if (dt is <= 0 or > 60 || _anterior.Count == 0)
        {
            Guardar(atual);
            return Array.Empty<ProcessoDisco>();
        }

        var lista = new List<ProcessoDisco>();

        foreach (var (pid, (lidos, escritos)) in atual)
        {
            if (!_anterior.TryGetValue(pid, out var antes)) continue;

            // Um PID reciclado pode dar diferenca negativa; descarta.
            if (lidos < antes.Lidos || escritos < antes.Escritos) continue;

            var leitura = (lidos - antes.Lidos) / dt / 1_048_576.0;
            var escrita = (escritos - antes.Escritos) / dt / 1_048_576.0;

            if (leitura + escrita < 0.05) continue;

            lista.Add(new ProcessoDisco(pid, nomes.GetValueOrDefault(pid, "?"), leitura, escrita));
        }

        Guardar(atual);

        return lista.OrderByDescending(x => x.TotalMbs).Take(quantos).ToList();
    }

    private void Guardar(Dictionary<int, (ulong, ulong)> atual)
    {
        _anterior.Clear();
        foreach (var (pid, valores) in atual) _anterior[pid] = valores;
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
}
