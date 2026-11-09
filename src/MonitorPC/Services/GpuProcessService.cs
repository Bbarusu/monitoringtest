using System.Diagnostics;
using System.Runtime.InteropServices;
using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed class GpuProcessService : IDisposable
{
    private const string CaminhoDeUso = @"\GPU Engine(*)\Utilization Percentage";
    private const string CaminhoDeMemoria = @"\GPU Process Memory(*)\Dedicated Usage";

    private const double UsoMinimo = 0.1;

    private const double VramMinimaMb = 50;

    private IntPtr _consulta;
    private IntPtr _uso;
    private IntPtr _memoria;

    public bool Disponivel { get; private set; }

    public string? Motivo { get; private set; }

    public GpuProcessService() => Abrir();

    private void Abrir()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _consulta) != 0)
            {
                Motivo = "O Windows não abriu a consulta de contadores de desempenho.";
                return;
            }

            if (PdhAddEnglishCounter(_consulta, CaminhoDeUso, IntPtr.Zero, out _uso) != 0)
            {
                Motivo = "Este Windows não publica o contador \"GPU Engine\". " +
                         "Ele existe a partir do Windows 10 com driver WDDM 2.0.";
                Fechar();
                return;
            }

            if (PdhAddEnglishCounter(_consulta, CaminhoDeMemoria, IntPtr.Zero, out _memoria) != 0)
                _memoria = IntPtr.Zero;

            PdhCollectQueryData(_consulta);

            Disponivel = true;
        }
        catch (Exception ex)
        {
            Log.Erro("Abrir contadores de GPU", ex);
            Motivo = "Não foi possível falar com os contadores de desempenho do Windows.";
            Fechar();
        }
    }

    public IReadOnlyList<ProcessoGpu> Medir()
    {
        if (!Disponivel || PdhCollectQueryData(_consulta) != 0)
            return Array.Empty<ProcessoGpu>();

        var motores = new Dictionary<int, Dictionary<string, double>>();

        foreach (var (instancia, valor) in Ler(_uso))
        {
            if (valor <= 0 || PidDe(instancia) is not { } pid) continue;

            if (!motores.TryGetValue(pid, out var porMotor))
                motores[pid] = porMotor = new Dictionary<string, double>(StringComparer.Ordinal);

            var motor = MotorDe(instancia) ?? "GPU";
            porMotor[motor] = porMotor.GetValueOrDefault(motor) + valor;
        }

        var vram = new Dictionary<int, double>();

        foreach (var (instancia, valor) in Ler(_memoria))
        {
            if (valor <= 0 || PidDe(instancia) is not { } pid) continue;
            vram[pid] = vram.GetValueOrDefault(pid) + valor / 1_048_576.0;
        }

        var lista = new List<ProcessoGpu>();

        foreach (var pid in motores.Keys.Union(vram.Keys))
        {
            var porMotor = motores.GetValueOrDefault(pid);

            var maior = porMotor is { Count: > 0 }
                ? porMotor.MaxBy(x => x.Value)
                : default;

            var uso = porMotor is { Count: > 0 } ? maior.Value : 0;
            var memoria = vram.GetValueOrDefault(pid);

            if (uso < UsoMinimo && memoria < VramMinimaMb) continue;

            lista.Add(new ProcessoGpu(pid, NomeDe(pid), uso,
                                      porMotor is { Count: > 0 } ? maior.Key : null,
                                      memoria));
        }

        return lista
            .OrderByDescending(p => p.UsoPct)
            .ThenByDescending(p => p.VramMb)
            .ToList();
    }

    private static string NomeDe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private static int? PidDe(string instancia)
    {
        const string marca = "pid_";
        if (!instancia.StartsWith(marca, StringComparison.Ordinal)) return null;

        var fim = marca.Length;
        while (fim < instancia.Length && char.IsAsciiDigit(instancia[fim])) fim++;

        return int.TryParse(instancia.AsSpan(marca.Length, fim - marca.Length), out var pid)
               && pid > 0
            ? pid
            : null;
    }

    private static string? MotorDe(string instancia)
    {
        const string marca = "engtype_";

        var corte = instancia.LastIndexOf(marca, StringComparison.Ordinal);
        if (corte < 0) return null;

        var tipo = instancia[(corte + marca.Length)..];
        if (tipo.Length == 0) return null;

        var sublinhado = tipo.LastIndexOf('_');
        if (sublinhado > 0 && tipo[(sublinhado + 1)..].All(char.IsAsciiDigit))
            tipo = tipo[..sublinhado];

        return tipo.Length > 0 ? tipo : null;
    }

    private static List<(string Instancia, double Valor)> Ler(IntPtr contador)
    {
        var lista = new List<(string, double)>();
        if (contador == IntPtr.Zero) return lista;

        uint tamanho = 0;
        if (PdhGetFormattedCounterArray(contador, FormatoDouble, ref tamanho, out _, IntPtr.Zero)
            != MaisDados)
            return lista;

        var buffer = Marshal.AllocHGlobal((int)tamanho);

        try
        {
            if (PdhGetFormattedCounterArray(contador, FormatoDouble, ref tamanho, out var itens, buffer) != 0)
                return lista;

            var passo = Marshal.SizeOf<ItemDeContador>();

            for (var i = 0; i < itens; i++)
            {
                var item = Marshal.PtrToStructure<ItemDeContador>(buffer + i * passo);

                if (item.Estado > 1) continue;

                var instancia = Marshal.PtrToStringUni(item.Instancia);
                if (instancia is not null) lista.Add((instancia, item.Valor));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return lista;
    }

    public void Dispose() => Fechar();

    private void Fechar()
    {
        Disponivel = false;

        if (_consulta == IntPtr.Zero) return;

        try { PdhCloseQuery(_consulta); } catch { }

        _consulta = IntPtr.Zero;
        _uso = IntPtr.Zero;
        _memoria = IntPtr.Zero;
    }

    private const uint FormatoDouble = 0x00000200;
    private const uint MaisDados = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ItemDeContador
    {
        public IntPtr Instancia;
        public uint Estado;
        public double Valor;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? fonte, IntPtr usuario, out IntPtr consulta);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(
        IntPtr consulta, string caminho, IntPtr usuario, out IntPtr contador);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr consulta);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr contador, uint formato, ref uint tamanho, out uint itens, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr consulta);
}
