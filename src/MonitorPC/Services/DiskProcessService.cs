using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

    // O caminho do executavel nao muda enquanto o processo vive: fica guardado
    // por PID.
    private readonly Dictionary<int, string?> _caminhos = new();
    private DateTime _ultimaAmostra = DateTime.MinValue;

    /// <summary>
    /// A medicao roda fora da interface, e Reiniciar vem dela: os dicionarios
    /// precisam de trava. Um Dictionary mexido por duas threads nao so perde
    /// dados — ele pode entrar em laco infinito ao crescer.
    /// </summary>
    private readonly object _trava = new();

    /// <summary>Esquece as amostras, para a proxima leitura nao virar um pico falso.</summary>
    public void Reiniciar()
    {
        lock (_trava)
        {
            _anterior.Clear();
            _ultimaAmostra = DateTime.MinValue;
        }
    }

    /// <summary>Sem limite, devolve tudo que se mexeu no intervalo.</summary>
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
                // Processos protegidos do sistema negam acesso ao handle.
                // Nao da para medi-los, e tentar de novo nao ajuda.
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

                lista.Add(new ProcessoDisco(pid, nomes.GetValueOrDefault(pid, "?"),
                                            leitura, escrita));
            }

            Guardar(atual);

            topo = lista.OrderByDescending(x => x.TotalMbs).Take(quantos).ToList();
        }

        // O caminho e resolvido so para quem sobrou, e nunca dentro da trava:
        // sao poucos processos, e a consulta fala com o sistema.
        return topo.Select(p => p with { Caminho = Caminho(p.Pid) }).ToList();
    }

    /// <summary>
    /// Caminho do executavel, por PID.
    ///
    /// Antes isto saia de Process.MainModule, que enumera os modulos carregados
    /// do outro processo — caro, e feito para os trezentos processos da maquina
    /// na primeira medicao, na thread da interface. Era o travamento ao abrir a
    /// aba Disco.
    ///
    /// QueryFullProcessImageName responde com uma chamada so, atravessa a
    /// fronteira de 32 e 64 bits e se contenta com o acesso limitado de
    /// consulta, que o Windows concede para quase tudo que nao seja processo
    /// protegido.
    /// </summary>
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

    /// <summary>Chamado com a trava tomada.</summary>
    private void Guardar(Dictionary<int, (ulong, ulong)> atual)
    {
        _anterior.Clear();
        foreach (var (pid, valores) in atual) _anterior[pid] = valores;

        // PIDs que sumiram nao voltam: o cache de caminhos cresceria sem parar.
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

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: so para perguntar quem e.</summary>
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
