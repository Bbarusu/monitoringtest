using System.Collections.Concurrent;
using LibreHardwareMonitor.Hardware;
using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>
/// Le os sensores em intervalo adaptativo e publica um <see cref="Snapshot"/> a cada leitura.
/// Ponto unico de contato com a LibreHardwareMonitorLib.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Computer _pc;
    private readonly UpdateVisitor _visitor = new();
    private readonly CancellationTokenSource _cts = new();

    // Fixadas por nome na primeira leitura: a ordem dos sensores nao e estavel
    // e as colunas trocariam entre leituras.
    private List<string> _nomesVentoinhas = new();

    private int _ramDetectado, _discosDetectado, _ventoinhasDetectado;
    private double _wattsEstimados;
    private DateTime _ultimaLeitura = DateTime.UtcNow;
    private DateTime _ultimoLento = DateTime.MinValue;
    private double _whAcumulado;

    private volatile bool _painelAberto;

    public event Action<Snapshot>? Atualizado;

    public Snapshot? Ultimo { get; private set; }

    /// <summary>Quantidades em uso, com a origem (detectada ou manual).</summary>
    public (int Valor, bool Manual, int Detectado) ModulosRam { get; private set; }
    public (int Valor, bool Manual, int Detectado) Discos { get; private set; }
    public (int Valor, bool Manual, int Detectado) Ventoinhas { get; private set; }

    public SensorService(AppConfig cfg)
    {
        _cfg = cfg;
        _pc = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true
        };
    }

    public void Iniciar()
    {
        _pc.Open();
        _pc.Accept(_visitor);

        DetectarQuantidades();
        RecalcularEstimativa();

        Task.Run(LacoAsync);
    }

    /// <summary>O painel aberto le a cada 1 s; fechado, a cada 3 s.</summary>
    public void DefinirPainelAberto(bool aberto) => _painelAberto = aberto;

    // ------------------------------------------------------------------

    private async Task LacoAsync()
    {
        // A segunda leitura e o que gera o delta das taxas de disco.
        await Task.Delay(500);
        _pc.Accept(_visitor);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _pc.Accept(_visitor);
                var snap = Montar();
                Ultimo = snap;
                Atualizado?.Invoke(snap);
            }
            catch
            {
                // Uma leitura que falha nao pode parar o laco.
            }

            var intervalo = _painelAberto ? _cfg.IntervaloPainelAberto : _cfg.IntervaloPainelFechado;
            try { await Task.Delay(intervalo, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private Snapshot Montar()
    {
        var agora = DateTime.UtcNow;
        var dt = (agora - _ultimaLeitura).TotalSeconds;
        _ultimaLeitura = agora;

        var cpuW = Pegar(h => h == HardwareType.Cpu, SensorType.Power, "Package");
        var gpuW = Pegar(EhGpu, SensorType.Power, "Package", "Board", "GPU");

        var total = (cpuW ?? 0) + (gpuW ?? 0) + _wattsEstimados;
        if (dt > 0 && dt < 60) _whAcumulado += total * dt / 3600.0;

        // Sensores lentos: vida util e espaco livre nao precisam de 1 s.
        if ((agora - _ultimoLento).TotalMilliseconds > _cfg.IntervaloSensoresLentos)
        {
            _ultimoLento = agora;
            DetectarQuantidades();
            RecalcularEstimativa();
        }

        var ventoinhas = new Dictionary<string, float>();
        foreach (var nome in _nomesVentoinhas)
        {
            var v = VentoinhaPorNome(nome);
            if (v.HasValue) ventoinhas[nome] = v.Value;
        }

        var cpuTemp = Pegar(h => h == HardwareType.Cpu, SensorType.Temperature, "Tctl", "Tdie", "Package");

        return new Snapshot
        {
            CpuTemp = cpuTemp,
            CpuUso = Pegar(h => h == HardwareType.Cpu, SensorType.Load, "CPU Total"),
            CpuWatts = cpuW,
            CpuClockMhz = Pegar(h => h == HardwareType.Cpu, SensorType.Clock, "Cores (Average)"),
            CpuClocksPorNucleo = ClocksPorNucleo(),
            CpuCargaPorThread = CargasPorThread(),
            CpuPicoDeNucleo = Pegar(h => h == HardwareType.Cpu, SensorType.Load, "CPU Core Max"),

            GpuNome = _pc.Hardware.FirstOrDefault(h => EhGpu(h.HardwareType))?.Name,
            GpuTemp = Pegar(EhGpu, SensorType.Temperature, "GPU Core", "Core"),
            GpuHotSpot = Pegar(EhGpu, SensorType.Temperature, "Hot"),
            GpuUso = Pegar(EhGpu, SensorType.Load, "GPU Core"),
            GpuWatts = gpuW,
            VramUsadaMb = Pegar(EhGpu, SensorType.SmallData, "GPU Memory Used"),
            VramTotalMb = Pegar(EhGpu, SensorType.SmallData, "GPU Memory Total"),
            GpuFanRpm = Pegar(EhGpu, SensorType.Fan, "Fan"),

            RamUso = Pegar(h => h == HardwareType.Memory, SensorType.Load, "Memory"),
            RamUsadaGb = Pegar(h => h == HardwareType.Memory, SensorType.Data, "Memory Used"),
            RamTotalGb = TotalRamGb(),

            DiscoTempMaior = TemperaturaDeDisco(),
            DiscoAtividade = AtividadeDeDisco(),

            Ventoinhas = ventoinhas,

            WattsEstimados = _wattsEstimados,
            WattsTotais = total,
            WhAcumulado = _whAcumulado,

            // Sem o driver, a temperatura da CPU volta zerada e o chip da placa-mae
            // nem aparece. E assim que a falha se manifesta.
            SensoresDeBaixoNivel = cpuTemp is > 0
        };
    }

    // ------------------------------------------------------------------
    // Quantidades e estimativa

    private void DetectarQuantidades()
    {
        _ramDetectado = _pc.Hardware.Count(h =>
            h.HardwareType == HardwareType.Memory &&
            h.Sensors.Any(s => s.SensorType == SensorType.Timing));

        _discosDetectado = _pc.Hardware.Count(h => h.HardwareType == HardwareType.Storage);

        _nomesVentoinhas = Todos()
            .Where(x => x.Sensor.SensorType == SensorType.Fan
                        && x.Sensor.Value > 0
                        && !EhGpu(x.Hardware.HardwareType))
            .Select(x => x.Sensor.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        _ventoinhasDetectado = _nomesVentoinhas.Count;

        ModulosRam = (_cfg.ModulosRamManual ?? _ramDetectado, _cfg.ModulosRamManual.HasValue, _ramDetectado);
        Discos = (_cfg.DiscosManual ?? _discosDetectado, _cfg.DiscosManual.HasValue, _discosDetectado);
        Ventoinhas = (_cfg.VentoinhasManual ?? _ventoinhasDetectado, _cfg.VentoinhasManual.HasValue, _ventoinhasDetectado);
    }

    /// <summary>Soma o que nenhum sensor mede. CPU e GPU ficam de fora: sao medidas.</summary>
    public void RecalcularEstimativa() => _wattsEstimados =
        ModulosRam.Valor * _cfg.WattsPorModuloRam +
        Discos.Valor * _cfg.WattsPorDisco +
        Ventoinhas.Valor * _cfg.WattsPorVentoinha +
        _cfg.PecasComLed * _cfg.WattsPorLed +
        (_cfg.TemWaterCooler ? _cfg.WattsBombaWaterCooler : 0) +
        _cfg.WattsPlacaMae +
        _cfg.WattsOutros;

    // ------------------------------------------------------------------
    // Leitura

    private IEnumerable<(IHardware Hardware, ISensor Sensor)> Todos()
    {
        var pilha = new Stack<IHardware>(_pc.Hardware);
        while (pilha.Count > 0)
        {
            var h = pilha.Pop();
            foreach (var s in h.Sensors) yield return (h, s);
            foreach (var sub in h.SubHardware) pilha.Push(sub);
        }
    }

    private static bool EhGpu(HardwareType t) =>
        t is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;

    private float? Pegar(Func<HardwareType, bool> filtro, SensorType tipo, params string[] nomes)
    {
        foreach (var nome in nomes)
            foreach (var (h, s) in Todos())
                if (filtro(h.HardwareType) && s.SensorType == tipo && s.Value.HasValue &&
                    s.Name.Contains(nome, StringComparison.OrdinalIgnoreCase))
                    return s.Value;
        return null;
    }

    private float? VentoinhaPorNome(string nome)
    {
        foreach (var (h, s) in Todos())
            if (s.SensorType == SensorType.Fan && !EhGpu(h.HardwareType) && s.Name == nome)
                return s.Value;
        return null;
    }

    /// <summary>
    /// Maior temperatura entre os discos, descartando os limiares do fabricante
    /// (Warning/Critical) e os sensores secundarios do controlador, que retornam
    /// valores fixos e altos.
    /// </summary>
    private float? TemperaturaDeDisco() => Todos()
        .Where(x => x.Hardware.HardwareType == HardwareType.Storage
                    && x.Sensor.SensorType == SensorType.Temperature
                    && x.Sensor.Value.HasValue
                    && !x.Sensor.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                    && !x.Sensor.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                    && !x.Sensor.Name.Contains('#'))
        .Select(x => x.Sensor.Value)
        .DefaultIfEmpty(null)
        .Max();

    private float? AtividadeDeDisco() => Todos()
        .Where(x => x.Hardware.HardwareType == HardwareType.Storage
                    && x.Sensor.SensorType == SensorType.Load
                    && x.Sensor.Name.Contains("Total Activity")
                    && x.Sensor.Value.HasValue)
        .Select(x => x.Sensor.Value)
        .DefaultIfEmpty(null)
        .Max();

    private float? TotalRamGb()
    {
        var usada = Pegar(h => h == HardwareType.Memory, SensorType.Data, "Memory Used");
        var livre = Pegar(h => h == HardwareType.Memory, SensorType.Data, "Memory Available");
        return usada.HasValue && livre.HasValue ? usada + livre : null;
    }

    /// <summary>Clocks por nucleo fisico. A contagem vem do hardware, nunca fixa.</summary>
    private List<float> ClocksPorNucleo() => Todos()
        .Where(x => x.Hardware.HardwareType == HardwareType.Cpu
                    && x.Sensor.SensorType == SensorType.Clock
                    && x.Sensor.Name.StartsWith("Core #")
                    && !x.Sensor.Name.Contains("Effective")
                    && x.Sensor.Value.HasValue)
        .Select(x => x.Sensor.Value!.Value)
        .ToList();

    /// <summary>Cargas por thread logica. Idem: a contagem vem do hardware.</summary>
    private List<float> CargasPorThread() => Todos()
        .Where(x => x.Hardware.HardwareType == HardwareType.Cpu
                    && x.Sensor.SensorType == SensorType.Load
                    && x.Sensor.Name.StartsWith("CPU Core #")
                    && x.Sensor.Value.HasValue)
        .Select(x => x.Sensor.Value!.Value)
        .ToList();

    public void Dispose()
    {
        _cts.Cancel();
        try { _pc.Close(); } catch { }
        _cts.Dispose();
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
