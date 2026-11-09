using System.Collections.Concurrent;
using LibreHardwareMonitor.Hardware;
using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed class SensorService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Computer _pc;
    private readonly UpdateVisitor _visitor = new();
    private readonly CancellationTokenSource _cts = new();

    private List<string> _nomesVentoinhas = new();

    private int _ramDetectado, _discosDetectado, _ventoinhasDetectado;
    private double _wattsEstimados;
    private DateTime _ultimaLeitura = DateTime.UtcNow;
    private DateTime _ultimoLento = DateTime.MinValue;
    private double _whAcumulado;
    private double _wattsSoma;
    private double _wattsPico;
    private long _leiturasDeEnergia;

    private volatile bool _painelAberto;

    public event Action<Snapshot>? Atualizado;

    public Snapshot? Ultimo { get; private set; }

    public (int Valor, bool Manual, int Detectado) ModulosRam { get; private set; }
    public (int Valor, bool Manual, int Detectado) Discos { get; private set; }
    public (int Valor, bool Manual, int Detectado) Ventoinhas { get; private set; }

    public EspecificacaoService Especificacoes { get; }

    public ConsumoService Consumo { get; } = new();

    private CalibracaoService? _calibracao;

    public CalibracaoService Calibracao => _calibracao ??= new CalibracaoService(this);

    public SensorService(AppConfig cfg)
    {
        _cfg = cfg;
        Especificacoes = new EspecificacaoService(cfg);
        _pc = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true
        };
    }

    public string SalvarDiagnostico() => Diagnostico.Salvar(_pc);

    private void GravarDiagnosticoSeFaltamSensores()
    {
        try
        {
            var temTemperatura = Todos().Any(t =>
                t.Hardware.HardwareType == HardwareType.Cpu &&
                t.Sensor.SensorType == SensorType.Temperature &&
                t.Sensor.Value.HasValue);

            if (temTemperatura) return;

            Log.Nota("Nenhuma temperatura de CPU na primeira leitura: o driver de " +
                     "sensores não subiu. Diagnóstico gravado em " + Diagnostico.Salvar(_pc));
        }
        catch (Exception ex)
        {
            Log.Erro("Gravar diagnóstico inicial", ex);
        }
    }

    public void Iniciar()
    {
        var pawnIo = Diagnostico.GarantirPawnIo();

        if (pawnIo != "já estava rodando") Log.Nota($"PawnIO: {pawnIo}.");

        _pc.Open();
        _pc.Accept(_visitor);

        GravarDiagnosticoSeFaltamSensores();

        DetectarQuantidades();
        Especificacoes.Identificar(
            _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name,
            _pc.Hardware.FirstOrDefault(h => EhGpu(h.HardwareType))?.Name);
        RecalcularEstimativa();

        Task.Run(LacoAsync);
    }

    public List<GrupoDeSensores> Tudo()
    {
        var grupos = new List<GrupoDeSensores>();

        void Visitar(IHardware h, string? prefixo = null)
        {
            var nome = prefixo is null ? h.Name.Trim() : $"{prefixo} › {h.Name.Trim()}";

            var leituras = h.Sensors
                .Where(x => x.Value.HasValue)
                .OrderBy(x => x.SensorType)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new LeituraDeSensor(x.Name, x.SensorType.ToString(),
                                                 x.Value!.Value, Unidade(x.SensorType)))
                .ToList();

            if (leituras.Count > 0)
                grupos.Add(new GrupoDeSensores(nome, h.HardwareType.ToString(), leituras));

            foreach (var sub in h.SubHardware) Visitar(sub, nome);
        }

        foreach (var h in _pc.Hardware) Visitar(h);

        return grupos;
    }

    private static string Unidade(SensorType tipo) => tipo switch
    {
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => "Hz",
        SensorType.Fan => "rpm",
        SensorType.Flow => "L/h",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Energy => "mWh",
        SensorType.Noise => "dBA",
        SensorType.Conductivity => "µS/cm",
        SensorType.Humidity => "%",
        _ => ""
    };

    public void AplicarConfiguracao()
    {
        DetectarQuantidades();
        RecalcularEstimativa();
    }

    public void DefinirPainelAberto(bool aberto) => _painelAberto = aberto;

    private int _rapido;

    public IDisposable ModoRapido()
    {
        Interlocked.Increment(ref _rapido);
        return new Devolucao(() => Interlocked.Decrement(ref _rapido));
    }

    private sealed class Devolucao(Action aoFechar) : IDisposable
    {
        private Action? _aoFechar = aoFechar;

        public void Dispose()
        {
            Interlocked.Exchange(ref _aoFechar, null)?.Invoke();
        }
    }

    private async Task LacoAsync()
    {
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
            }

            var intervalo = _painelAberto || Volatile.Read(ref _rapido) > 0
                ? _cfg.IntervaloPainelAberto
                : _cfg.IntervaloPainelFechado;
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

        var clocksPorNucleo = ClocksPorNucleo();

        var clockMedio = Pegar(h => h == HardwareType.Cpu, SensorType.Clock, "Cores (Average)")
                         ?? (clocksPorNucleo.Count > 0
                             ? clocksPorNucleo.Average()
                             : (float?)null);

        var vramUsada = Pegar(EhGpu, SensorType.SmallData, "GPU Memory Used");
        var vramTotal = Pegar(EhGpu, SensorType.SmallData, "GPU Memory Total");
        var vramCompartilhada = vramUsada is null || vramTotal is null;

        if (vramCompartilhada)
        {
            vramUsada = Pegar(EhGpu, SensorType.SmallData, "D3D Shared Memory Used");
            vramTotal = Pegar(EhGpu, SensorType.SmallData, "D3D Shared Memory Total");
        }

        var total = (cpuW ?? 0) + (gpuW ?? 0) + _wattsEstimados;
        if (dt > 0 && dt < 60)
        {
            _whAcumulado += total * dt / 3600.0;
            _wattsSoma += total;
            _wattsPico = Math.Max(_wattsPico, total);
            _leiturasDeEnergia++;

            Consumo.Somar(total, dt);
        }

        if ((agora - _ultimoLento).TotalMilliseconds > _cfg.IntervaloSensoresLentos)
        {
            _ultimoLento = agora;
            DetectarQuantidades();
            RecalcularEstimativa();

            Consumo.Gravar();
        }

        var pares = ParesDeVentoinha();

        foreach (var p in pares)
            Especificacoes.ObservarVentoinha(p.Chave, p.Rpm, p.Pwm);

        AnotarVentoinhasConectadas(pares);

        var ventoinhas = new Dictionary<string, float>();
        var controles = new Dictionary<string, float>();
        foreach (var nome in _nomesVentoinhas)
        {
            var par = pares.FirstOrDefault(p => !p.Gpu && p.Nome == nome);
            if (par is null) continue;

            ventoinhas[nome] = par.Rpm;
            if (par.Pwm is { } pwm) controles[nome] = pwm;
        }

        var gpuFan = pares.FirstOrDefault(p => p.Gpu);

        var cpuTemp = Pegar(h => h == HardwareType.Cpu, SensorType.Temperature, "Tctl", "Tdie", "Package");
        var discos = MontarDiscos();

        return new Snapshot
        {
            CpuTemp = cpuTemp,
            CpuUso = Pegar(h => h == HardwareType.Cpu, SensorType.Load, "CPU Total"),
            CpuWatts = cpuW,
            CpuClockMhz = clockMedio,
            CpuClocksPorNucleo = clocksPorNucleo,
            CpuCargaPorThread = CargasPorThread(),
            CpuPicoDeNucleo = Pegar(h => h == HardwareType.Cpu, SensorType.Load, "CPU Core Max"),
            CpuTensao = TensaoDoNucleo(),

            GpuNome = _pc.Hardware.FirstOrDefault(h => EhGpu(h.HardwareType))?.Name,
            GpuTemp = Pegar(EhGpu, SensorType.Temperature, "GPU Core", "Core"),
            GpuHotSpot = Pegar(EhGpu, SensorType.Temperature, "Hot"),
            GpuUso = UsoDeGpu(),
            GpuWatts = gpuW,
            VramUsadaMb = vramUsada,
            VramTotalMb = vramTotal,
            VramCompartilhada = vramCompartilhada,
            GpuFanRpm = Pegar(EhGpu, SensorType.Fan, "Fan"),
            GpuFanPwm = gpuFan?.Pwm,
            GpuFanChave = gpuFan?.Chave,
            GpuClockNucleo = Pegar(EhGpu, SensorType.Clock, "GPU Core"),
            GpuClockMemoria = Pegar(EhGpu, SensorType.Clock, "GPU Memory"),
            GpuTempMemoria = Pegar(EhGpu, SensorType.Temperature, "GPU Memory"),

            RamUso = Pegar(h => h == HardwareType.Memory, SensorType.Load, "Memory"),
            RamUsadaGb = Pegar(h => h == HardwareType.Memory, SensorType.Data, "Memory Used"),
            RamTotalGb = TotalRamGb(),

            DiscoTempMaior = TemperaturaDeDisco(),
            DiscoAtividade = AtividadeDeDisco(),
            DiscoLeituraMbs = SomaDeDiscos(SensorType.Throughput, "Read Rate"),
            DiscoEscritaMbs = SomaDeDiscos(SensorType.Throughput, "Write Rate"),
            DiscoVidaMenor = discos.Where(d => d.VidaPct.HasValue)
                                   .Select(d => d.VidaPct)
                                   .DefaultIfEmpty(null)
                                   .Min(),
            Discos = discos,

            Ventoinhas = ventoinhas,
            ControlesDeVentoinha = controles,
            ChavesDeVentoinha = pares.Where(p => !p.Gpu)
                                     .GroupBy(p => p.Nome)
                                     .ToDictionary(g => g.Key, g => g.First().Chave),

            WattsEstimados = _wattsEstimados,
            WattsTotais = total,
            WhAcumulado = _whAcumulado,
            WhDeHoje = Consumo.WhDeHoje,
            WhRegistrado = Consumo.WhTotal,
            WattsMedia = _leiturasDeEnergia > 0 ? _wattsSoma / _leiturasDeEnergia : 0,
            WattsPico = _wattsPico,
            ModulosRam = ModulosRam.Valor,
            Discos2 = Discos.Valor,
            VentoinhasAtivas = Ventoinhas.Valor,

            SensoresDeBaixoNivel = cpuTemp is > 0
        };
    }

    private readonly HashSet<string> _ventoinhasConectadas = new(StringComparer.Ordinal);

    private void AnotarVentoinhasConectadas(List<ParDeVentoinha> pares)
    {
        var novidade = false;

        foreach (var p in pares)
            if (!p.Gpu && p.Rpm > 0 && _ventoinhasConectadas.Add(p.Nome))
                novidade = true;

        if (!novidade) return;

        _nomesVentoinhas = _ventoinhasConectadas.OrderBy(n => n, StringComparer.Ordinal).ToList();
        _ventoinhasDetectado = _nomesVentoinhas.Count;

        Ventoinhas = (_cfg.VentoinhasManual ?? _ventoinhasDetectado,
                      _cfg.VentoinhasManual.HasValue,
                      _ventoinhasDetectado);

        RecalcularEstimativa();
    }

    private void DetectarQuantidades()
    {
        _ramDetectado = _pc.Hardware.Count(h =>
            h.HardwareType == HardwareType.Memory &&
            h.Sensors.Any(s => s.SensorType == SensorType.Timing));

        _discosDetectado = _pc.Hardware.Count(h => h.HardwareType == HardwareType.Storage);

        ModulosRam = (_cfg.ModulosRamManual ?? _ramDetectado, _cfg.ModulosRamManual.HasValue, _ramDetectado);
        Discos = (_cfg.DiscosManual ?? _discosDetectado, _cfg.DiscosManual.HasValue, _discosDetectado);
        Ventoinhas = (_cfg.VentoinhasManual ?? _ventoinhasDetectado, _cfg.VentoinhasManual.HasValue, _ventoinhasDetectado);
    }

    public void RecalcularEstimativa() => _wattsEstimados =
        ModulosRam.Valor * _cfg.WattsPorModuloRam +
        Discos.Valor * _cfg.WattsPorDisco +
        Ventoinhas.Valor * _cfg.WattsPorVentoinha +
        _cfg.PecasComLed * _cfg.WattsPorLed +
        (_cfg.TemWaterCooler ? _cfg.WattsBombaWaterCooler : 0) +
        _cfg.WattsPlacaMae +
        _cfg.WattsOutros;

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

    private float? UsoDeGpu()
    {
        if (Pegar(EhGpu, SensorType.Load, "GPU Core") is { } core) return core;

        float? maior = null;

        foreach (var (hardware, sensor) in Todos())
        {
            if (!EhGpu(hardware.HardwareType)) continue;
            if (sensor.SensorType != SensorType.Load) continue;
            if (!sensor.Name.StartsWith("D3D", StringComparison.OrdinalIgnoreCase)) continue;
            if (sensor.Value is not { } valor) continue;

            if (maior is null || valor > maior) maior = valor;
        }

        return maior;
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

    public sealed record ParDeVentoinha(
        string Chave, string Nome, string Peca, bool Gpu, float Rpm, float? Pwm);

    public List<ParDeVentoinha> ParesDeVentoinha() => Canais()
        .Select(c => new ParDeVentoinha(
            Chave(c.Peca, c.Rotacao), c.Rotacao.Name, c.Peca.Name.Trim(),
            EhGpu(c.Peca.HardwareType), c.Rotacao.Value!.Value, c.Controle?.Value))
        .ToList();

    public sealed record CanalDeVentoinha(
        string Chave, string Nome, string Peca, bool Gpu,
        ISensor Rotacao, IControl? Comando, bool TemControle)
    {
        public float Rpm => Rotacao.Value ?? 0;

        public bool TemLeitura => Rotacao.Value.HasValue;

        public bool Comandavel => Comando is not null;
    }

    public List<CanalDeVentoinha> CanaisDeVentoinha() => Canais(incluirSemLeitura: true)
        .Select(c => new CanalDeVentoinha(
            Chave(c.Peca, c.Rotacao), c.Rotacao.Name, c.Peca.Name.Trim(),
            EhGpu(c.Peca.HardwareType), c.Rotacao, c.Controle?.Control,
            TemControle: c.Controle is not null))
        .ToList();

    private List<(IHardware Peca, ISensor Rotacao, ISensor? Controle)> Canais(
        bool incluirSemLeitura = false)
    {
        var lista = new List<(IHardware, ISensor, ISensor?)>();
        var pilha = new Stack<IHardware>(_pc.Hardware);

        while (pilha.Count > 0)
        {
            var h = pilha.Pop();
            foreach (var sub in h.SubHardware) pilha.Push(sub);

            var rotacoes = h.Sensors
                .Where(s => s.SensorType == SensorType.Fan
                            && (incluirSemLeitura || s.Value.HasValue))
                .OrderBy(s => s.Index)
                .ToList();

            if (rotacoes.Count == 0) continue;

            var controles = h.Sensors
                .Where(s => s.SensorType == SensorType.Control && s.Value.HasValue)
                .OrderBy(s => s.Index)
                .ToList();

            for (var i = 0; i < rotacoes.Count; i++)
                lista.Add((h, rotacoes[i], Controle(rotacoes[i], controles, i, rotacoes.Count)));
        }

        return lista;
    }

    private static string Chave(IHardware peca, ISensor fan) =>
        $"{peca.Name.Trim()}/{fan.Name.Trim()}";

    private static ISensor? Controle(ISensor fan, List<ISensor> controles, int i, int total)
    {
        if (controles.Count == 0) return null;

        var sufixo = Sufixo(fan.Name);
        if (sufixo is not null)
        {
            var igual = controles.FirstOrDefault(c => Sufixo(c.Name) == sufixo);
            if (igual is not null) return igual;
        }

        var nome = controles.FirstOrDefault(c =>
            string.Equals(c.Name, fan.Name, StringComparison.OrdinalIgnoreCase));
        if (nome is not null) return nome;

        return controles.Count == total ? controles[i] : null;
    }

    private static string? Sufixo(string nome)
    {
        var corte = nome.LastIndexOf('#');
        return corte >= 0 ? nome[corte..].Trim() : null;
    }

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

    private float? TensaoDoNucleo()
    {
        var vids = Todos()
            .Where(x => x.Hardware.HardwareType == HardwareType.Cpu
                        && x.Sensor.SensorType == SensorType.Voltage
                        && x.Sensor.Name.Contains("VID", StringComparison.OrdinalIgnoreCase)
                        && x.Sensor.Value > 0)
            .Select(x => x.Sensor.Value)
            .DefaultIfEmpty(null)
            .Max();

        return vids
               ?? Pegar(h => h == HardwareType.Cpu, SensorType.Voltage, "SVI2 TFN")
               ?? PegarExato(h => h == HardwareType.Cpu, SensorType.Voltage, "CPU Core")
               ?? Pegar(_ => true, SensorType.Voltage, "Vcore");
    }

    private float? PegarExato(Func<HardwareType, bool> filtro, SensorType tipo, string nome)
    {
        foreach (var (hardware, sensor) in Todos())
            if (filtro(hardware.HardwareType)
                && sensor.SensorType == tipo
                && sensor.Value.HasValue
                && string.Equals(sensor.Name, nome, StringComparison.OrdinalIgnoreCase))
                return sensor.Value;

        return null;
    }

    private float? SomaDeDiscos(SensorType tipo, string nome)
    {
        var valores = Todos()
            .Where(x => x.Hardware.HardwareType == HardwareType.Storage
                        && x.Sensor.SensorType == tipo
                        && x.Sensor.Name.Contains(nome, StringComparison.OrdinalIgnoreCase)
                        && x.Sensor.Value.HasValue)
            .Select(x => x.Sensor.Value!.Value)
            .ToList();

        return valores.Count == 0 ? null : valores.Sum() / 1_048_576f;
    }

    private List<DiscoInfo> MontarDiscos()
    {
        var lista = new List<DiscoInfo>();

        foreach (var h in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            float? Sensor(SensorType tipo, params string[] nomes)
            {
                foreach (var nome in nomes)
                    foreach (var s in h.Sensors)
                        if (s.SensorType == tipo && s.Value.HasValue &&
                            s.Name.Contains(nome, StringComparison.OrdinalIgnoreCase))
                            return s.Value;
                return null;
            }

            var temp = h.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue
                            && !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                            && !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                            && !s.Name.Contains('#'))
                .Select(s => s.Value)
                .DefaultIfEmpty(null)
                .Max();

            var leitura = Sensor(SensorType.Throughput, "Read Rate");
            var escrita = Sensor(SensorType.Throughput, "Write Rate");

            lista.Add(new DiscoInfo(
                h.Name.Trim(),
                temp,
                Sensor(SensorType.Load, "Total Activity"),
                leitura / 1_048_576f,
                escrita / 1_048_576f,
                Sensor(SensorType.Level, "Life"),
                Sensor(SensorType.Factor, "Power On Hours"),
                Sensor(SensorType.Load, "Used Space"),
                Sensor(SensorType.Data, "Free Space"),
                Sensor(SensorType.Data, "Total Space"),

                Limiar(h, "Warning"),
                Limiar(h, "Critical")));
        }

        return lista;
    }

    private static float? Limiar(IHardware h, string tipo) => h.Sensors
        .Where(s => s.SensorType == SensorType.Temperature
                    && s.Name.Contains(tipo, StringComparison.OrdinalIgnoreCase)
                    && s.Value is > 40 and < 130)
        .Select(s => s.Value)
        .DefaultIfEmpty(null)
        .Max();

    private float? TotalRamGb()
    {
        var usada = Pegar(h => h == HardwareType.Memory, SensorType.Data, "Memory Used");
        var livre = Pegar(h => h == HardwareType.Memory, SensorType.Data, "Memory Available");
        return usada.HasValue && livre.HasValue ? usada + livre : null;
    }

    private List<float> ClocksPorNucleo() => Todos()
        .Where(x => x.Hardware.HardwareType == HardwareType.Cpu
                    && x.Sensor.SensorType == SensorType.Clock
                    && EhClockDeNucleo(x.Sensor.Name)
                    && x.Sensor.Value.HasValue)
        .Select(x => x.Sensor.Value!.Value)
        .ToList();

    private static bool EhClockDeNucleo(string nome) =>
        nome.Contains("Core #", StringComparison.OrdinalIgnoreCase)
        && !nome.Contains("Effective", StringComparison.OrdinalIgnoreCase);

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

        try { _calibracao?.Restaurar(); } catch { }

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
