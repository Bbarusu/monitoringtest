using System.Diagnostics;
using System.Runtime.InteropServices;
using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>
/// Quanto cada programa usa da placa de video, pelos contadores de desempenho
/// do proprio Windows — os mesmos que o Gerenciador de Tarefas le.
///
/// Nao ha sensor para isto. A placa informa a ocupacao dela como um todo; quem
/// sabe repartir por processo e o agendador grafico do Windows, que publica o
/// resultado em "GPU Engine" e "GPU Process Memory". Funciona com qualquer
/// placa, AMD, NVIDIA ou integrada, sem biblioteca de fabricante.
///
/// Os nomes dos contadores sao pedidos em ingles de proposito: num Windows em
/// portugues os objetos aparecem traduzidos, e um caminho montado com o nome
/// traduzido so funcionaria naquele idioma. PdhAddEnglishCounter resolve isso.
/// </summary>
public sealed class GpuProcessService : IDisposable
{
    private const string CaminhoDeUso = @"\GPU Engine(*)\Utilization Percentage";
    private const string CaminhoDeMemoria = @"\GPU Process Memory(*)\Dedicated Usage";

    /// <summary>Abaixo disto o processo so encosta na placa; nao vale uma linha.</summary>
    private const double UsoMinimo = 0.1;

    /// <summary>Mas quem segura VRAM aparece mesmo sem estar usando agora.</summary>
    private const double VramMinimaMb = 50;

    private IntPtr _consulta;
    private IntPtr _uso;
    private IntPtr _memoria;

    /// <summary>Falso quando o Windows nao publica estes contadores.</summary>
    public bool Disponivel { get; private set; }

    /// <summary>Por que nao da, em linguagem de gente.</summary>
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

            // A memoria por processo e um extra: se faltar, o uso ainda serve.
            if (PdhAddEnglishCounter(_consulta, CaminhoDeMemoria, IntPtr.Zero, out _memoria) != 0)
                _memoria = IntPtr.Zero;

            // A ocupacao e medida por tempo, entao a primeira coleta so marca o
            // ponto de partida: sem ela, a primeira leitura sairia zerada.
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

    /// <summary>
    /// Uma leitura. Precisa de pelo menos um segundo desde a anterior: a
    /// ocupacao e a fracao de tempo que o processo ocupou a placa no intervalo,
    /// e um intervalo curto demais da um numero instavel.
    /// </summary>
    public IReadOnlyList<ProcessoGpu> Medir()
    {
        if (!Disponivel || PdhCollectQueryData(_consulta) != 0)
            return Array.Empty<ProcessoGpu>();

        // Por processo e por motor. Somar os motores daria mais de 100% num
        // jogo que usa 3D e Copy ao mesmo tempo, entao vale o maior — e dizer
        // qual e ele informa mais que a soma.
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

    // ------------------------------------------------------------------

    private static string NomeDe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            // O processo pode ter terminado entre a coleta e esta consulta.
            return $"PID {pid}";
        }
    }

    /// <summary>
    /// O numero logo depois de "pid_". O resto do nome da instancia muda entre
    /// versoes do Windows e entre drivers — ja foi visto como
    /// "pid_1234_engtype_3D_0" e como
    /// "pid_1234_luid_0x0_0xABCD_phys_0_eng_0_engtype_3D" —, entao so se lê o
    /// que e estavel.
    /// </summary>
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

    /// <summary>O que vem depois de "engtype_", sem o índice do motor no fim.</summary>
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

    // ------------------------------------------------------------------
    // PDH

    private static List<(string Instancia, double Valor)> Ler(IntPtr contador)
    {
        var lista = new List<(string, double)>();
        if (contador == IntPtr.Zero) return lista;

        // Primeira chamada com buffer nulo: o PDH responde de quanto precisa.
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

                // 0 e dado valido; 1 e dado novo. O resto nao serve.
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

    /// <summary>
    /// PDH_FMT_COUNTERVALUE_ITEM_W. O alinhamento natural de 64 bits poe quatro
    /// bytes de enchimento entre o estado e o valor, que e o mesmo que o C faz.
    /// </summary>
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
