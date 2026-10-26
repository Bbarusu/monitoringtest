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

    // Fixadas por nome, e nao por posicao: a ordem dos sensores nao e estavel e
    // as colunas trocariam entre leituras. Quem alimenta esta lista e
    // AnotarVentoinhasConectadas, conforme os canais giram.
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

    /// <summary>Quantidades em uso, com a origem (detectada ou manual).</summary>
    public (int Valor, bool Manual, int Detectado) ModulosRam { get; private set; }
    public (int Valor, bool Manual, int Detectado) Discos { get; private set; }
    public (int Valor, bool Manual, int Detectado) Ventoinhas { get; private set; }

    /// <summary>
    /// Tetos de escala e curvas aprendidas. Fica aqui porque so este servico
    /// toca na biblioteca de sensores, e e dela que vem a materia-prima.
    /// </summary>
    public EspecificacaoService Especificacoes { get; }

    /// <summary>
    /// Registro de consumo por dia, em arquivo. Fica aqui porque e aqui que o
    /// intervalo entre duas leituras e conhecido, e e dele que sai o watt-hora.
    /// </summary>
    public ConsumoService Consumo { get; } = new();

    private CalibracaoService? _calibracao;

    /// <summary>
    /// Medicao da rotacao maxima das ventoinhas. Fica pendurada aqui para que o
    /// descarte do servico — que acontece no fechamento do app — tambem desfaca
    /// qualquer comando de PWM que tenha ficado pelo caminho.
    /// </summary>
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

    /// <summary>
    /// Grava o relatorio de diagnostico e devolve o caminho.
    ///
    /// Mora aqui porque o <c>Computer</c> da biblioteca nao sai desta classe: o
    /// relatorio dele e a unica fonte que diz o erro exato do carregamento do
    /// driver, e expor o objeto inteiro so para isso abriria caminho para o
    /// resto do app mexer no estado dos sensores por fora.
    /// </summary>
    public string SalvarDiagnostico() => Diagnostico.Salvar(_pc);

    /// <summary>
    /// Sem temperatura de CPU na primeira leitura, grava o diagnostico sozinho.
    ///
    /// A biblioteca nao lanca excecao quando o driver nao sobe: ela devolve a
    /// lista de sensores vazia e segue. Por isso o erro.log de uma maquina que
    /// falha nao tinha uma linha sobre o driver — nao havia o que registrar, e
    /// o motivo ficava preso dentro do relatorio interno dela, que ninguem
    /// pedia.
    ///
    /// Agora o arquivo passa a existir sozinho em qualquer maquina onde a
    /// leitura falha, pronto para ser enviado, sem depender de alguem achar o
    /// botao no painel. Numa maquina que le normalmente isto nao custa nada:
    /// sai na primeira linha.
    /// </summary>
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
            // Gravar o diagnostico nao pode impedir o app de abrir: sem driver
            // ainda restam GPU, disco, RAM e uso de CPU.
            Log.Erro("Gravar diagnóstico inicial", ex);
        }
    }

    public void Iniciar()
    {
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

    /// <summary>
    /// Tudo que os sensores reportam agora, agrupado por componente.
    ///
    /// O painel mostra o que cabe nas abas, mas o hardware expoe bem mais:
    /// temperaturas e tensoes da placa-mae, potencia por nucleo, horas de disco.
    /// Isto entrega a lista inteira, sem escolher por voce.
    /// </summary>
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

    /// <summary>A unidade que a biblioteca usa para cada tipo de sensor.</summary>
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

    /// <summary>
    /// Relê as quantidades e refaz a estimativa. Chamada quando a configuracao
    /// muda, para o painel nao esperar o proximo ciclo de sensores lentos.
    /// </summary>
    public void AplicarConfiguracao()
    {
        DetectarQuantidades();
        RecalcularEstimativa();
    }

    /// <summary>O painel aberto le a cada 1 s; fechado, a cada 3 s.</summary>
    public void DefinirPainelAberto(bool aberto) => _painelAberto = aberto;

    private int _rapido;

    /// <summary>
    /// Leitura no intervalo curto enquanto o retorno nao for descartado, mesmo
    /// com o painel fechado. A calibracao precisa disso: medir a aceleracao de
    /// uma ventoinha de 3 em 3 segundos perderia o momento em que ela estabiliza.
    /// E um contador, e nao uma chave, para dois pedidos simultaneos nao se
    /// desligarem um ao outro.
    /// </summary>
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

        var total = (cpuW ?? 0) + (gpuW ?? 0) + _wattsEstimados;
        if (dt > 0 && dt < 60)
        {
            _whAcumulado += total * dt / 3600.0;
            _wattsSoma += total;
            _wattsPico = Math.Max(_wattsPico, total);
            _leiturasDeEnergia++;

            Consumo.Somar(total, dt);
        }

        // Sensores lentos: vida util e espaco livre nao precisam de 1 s.
        if ((agora - _ultimoLento).TotalMilliseconds > _cfg.IntervaloSensoresLentos)
        {
            _ultimoLento = agora;
            DetectarQuantidades();
            RecalcularEstimativa();

            // Grava junto com os sensores lentos, nao a cada leitura: um arquivo
            // de poucos kilobytes reescrito de 30 em 30 segundos nao se nota, e
            // uma queda de energia perde no maximo meio minuto de registro.
            Consumo.Gravar();
        }

        var pares = ParesDeVentoinha();

        // Cada par rotacao/PWM melhora a estimativa da rotacao maxima. E a unica
        // forma de saber o teto de um ventilador: nenhuma tabela sabe qual
        // modelo esta espetado em qual conector da placa.
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
            CpuClockMhz = Pegar(h => h == HardwareType.Cpu, SensorType.Clock, "Cores (Average)"),
            CpuClocksPorNucleo = ClocksPorNucleo(),
            CpuCargaPorThread = CargasPorThread(),
            CpuPicoDeNucleo = Pegar(h => h == HardwareType.Cpu, SensorType.Load, "CPU Core Max"),
            CpuTensao = TensaoDoNucleo(),

            GpuNome = _pc.Hardware.FirstOrDefault(h => EhGpu(h.HardwareType))?.Name,
            GpuTemp = Pegar(EhGpu, SensorType.Temperature, "GPU Core", "Core"),
            GpuHotSpot = Pegar(EhGpu, SensorType.Temperature, "Hot"),
            GpuUso = UsoDeGpu(),
            GpuWatts = gpuW,
            VramUsadaMb = Pegar(EhGpu, SensorType.SmallData, "GPU Memory Used"),
            VramTotalMb = Pegar(EhGpu, SensorType.SmallData, "GPU Memory Total"),
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
            // Agrupado por nome porque duas controladoras podem expor sensores
            // homonimos ("Fan #1" em cada uma), e um dicionario com chave
            // repetida derrubaria a leitura inteira.
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

            // Sem o driver, a temperatura da CPU volta zerada e o chip da placa-mae
            // nem aparece. E assim que a falha se manifesta.
            SensoresDeBaixoNivel = cpuTemp is > 0
        };
    }

    // ------------------------------------------------------------------
    // Quais ventoinhas existem de verdade

    /// <summary>
    /// Canais que ja giraram desde que o app abriu. Um conector vazio nunca
    /// entra; uma ventoinha que esta parada agora continua dentro.
    /// </summary>
    private readonly HashSet<string> _ventoinhasConectadas = new(StringComparer.Ordinal);

    /// <summary>
    /// Decide quais canais sao ventoinhas de verdade, pelo que giraram.
    ///
    /// A deteccao antiga olhava a leitura do momento e congelava: rodava pela
    /// primeira vez logo depois de abrir a biblioteca, que e justamente a
    /// leitura menos confiavel de um chip de placa-mae — canais sem nada
    /// espetado podem devolver lixo ali — e depois refazia a lista a cada 30
    /// segundos, com a mesma regra. Isso trazia conector vazio para dentro e
    /// fazia a contagem oscilar quando uma ventoinha parava.
    ///
    /// Girar e a unica prova de que ha ventoinha no conector, e ela nao
    /// expira: o conjunto so cresce, e por isso uma ventoinha em modo
    /// silencioso entra assim que acorda e nao sai quando volta a parar.
    /// </summary>
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

    // ------------------------------------------------------------------
    // Quantidades e estimativa

    private void DetectarQuantidades()
    {
        _ramDetectado = _pc.Hardware.Count(h =>
            h.HardwareType == HardwareType.Memory &&
            h.Sensors.Any(s => s.SensorType == SensorType.Timing));

        _discosDetectado = _pc.Hardware.Count(h => h.HardwareType == HardwareType.Storage);

        // As ventoinhas nao sao contadas aqui: quem decide quais existem e o
        // laco de leitura, em AnotarVentoinhasConectadas.
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

    /// <summary>
    /// Uso da GPU, incluindo o caminho das integradas.
    ///
    /// NVIDIA e AMD publicam uma carga chamada "GPU Core", e era a unica que
    /// este app lia. GPU integrada da Intel nao tem esse sensor: ela publica
    /// uma carga por motor do D3D — "D3D 3D", "D3D Video Decode", "D3D Copy" e
    /// outros. Por isso, num PC de video integrado, a peca aparecia na aba de
    /// todos os sensores mas o uso vinha vazio, e com ele vazio o cartao da aba
    /// Geral mostrava "--" como se nao houvesse GPU.
    ///
    /// Sem "GPU Core", vale o motor mais ocupado. E o que o Gerenciador de
    /// Tarefas do Windows chama de porcentagem de GPU, entao o numero bate com
    /// o que o usuario ja conhece de outro lugar. Somar os motores passaria de
    /// 100% e nao significaria nada.
    /// </summary>
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

    /// <summary>Uma ventoinha e o canal de PWM que a comanda, quando existe.</summary>
    public sealed record ParDeVentoinha(
        string Chave, string Nome, string Peca, bool Gpu, float Rpm, float? Pwm);

    /// <summary>
    /// Casa cada sensor de rotacao com o sensor de controle correspondente.
    ///
    /// A placa reporta os dois separados, e nada na biblioteca os liga. O que os
    /// liga e o sufixo: "Fan #2" e "Fan Control #2" sao o mesmo conector. Em
    /// placas de video os dois tem o mesmo nome. Quando nem um nem outro resolve,
    /// vale a posicao, mas so com a mesma quantidade dos dois lados — parear na
    /// marra daria um PWM de outro conector, e a extrapolacao sairia errada sem
    /// dar sinal de que errou.
    /// </summary>
    /// <summary>
    /// Inclui os canais parados: uma ventoinha ja reconhecida que esta a zero
    /// agora continua sendo uma ventoinha, e o painel precisa do PWM e da chave
    /// dela para desenhar "parada" com a escala certa.
    /// </summary>
    public List<ParDeVentoinha> ParesDeVentoinha() => Canais()
        .Select(c => new ParDeVentoinha(
            Chave(c.Peca, c.Rotacao), c.Rotacao.Name, c.Peca.Name.Trim(),
            EhGpu(c.Peca.HardwareType), c.Rotacao.Value!.Value, c.Controle?.Value))
        .ToList();

    /// <summary>
    /// Uma ventoinha e o canal que a comanda, para a calibracao. Diferente dos
    /// pares, inclui as que estao paradas: uma placa de video em modo silencioso
    /// marca 0 rpm, e e justamente ela que mais precisa ser calibrada.
    /// </summary>
    public sealed record CanalDeVentoinha(
        string Chave, string Nome, string Peca, bool Gpu,
        ISensor Rotacao, IControl? Comando, bool TemControle)
    {
        public float Rpm => Rotacao.Value ?? 0;

        /// <summary>A peca reporta rotacao agora. Zero conta; ausencia de leitura nao.</summary>
        public bool TemLeitura => Rotacao.Value.HasValue;

        /// <summary>
        /// Da para mandar nela. Diferente de <see cref="TemControle"/>: muita
        /// placa de video publica o PWM e nao aceita comando nenhum, e separar
        /// as duas coisas e o que permite dizer ao usuario onde a corrente
        /// arrebenta.
        /// </summary>
        public bool Comandavel => Comando is not null;
    }

    public List<CanalDeVentoinha> CanaisDeVentoinha() => Canais(incluirSemLeitura: true)
        .Select(c => new CanalDeVentoinha(
            Chave(c.Peca, c.Rotacao), c.Rotacao.Name, c.Peca.Name.Trim(),
            EhGpu(c.Peca.HardwareType), c.Rotacao, c.Controle?.Control,
            TemControle: c.Controle is not null))
        .ToList();

    /// <summary>
    /// Casa cada sensor de rotacao com o de controle, uma vez so, para os dois
    /// usos: aprender a curva e comandar a ventoinha.
    /// </summary>
    /// <param name="incluirSemLeitura">
    /// Traz tambem os canais de rotacao que nao estao reportando nada. Para
    /// aprender a curva eles sao inuteis, mas na tela de calibracao sumir em
    /// silencio e pior que aparecer como "sem leitura": o usuario fica
    /// procurando uma ventoinha que o app nunca mostrou.
    /// </param>
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

    /// <summary>Identidade estavel de uma ventoinha entre sessoes.</summary>
    private static string Chave(IHardware peca, ISensor fan) =>
        $"{peca.Name.Trim()}/{fan.Name.Trim()}";

    private static ISensor? Controle(ISensor fan, List<ISensor> controles, int i, int total)
    {
        if (controles.Count == 0) return null;

        // 1. Mesmo sufixo numerico: "Fan #2" com "Fan Control #2".
        var sufixo = Sufixo(fan.Name);
        if (sufixo is not null)
        {
            var igual = controles.FirstOrDefault(c => Sufixo(c.Name) == sufixo);
            if (igual is not null) return igual;
        }

        // 2. Mesmo nome, como nas placas de video.
        var nome = controles.FirstOrDefault(c =>
            string.Equals(c.Name, fan.Name, StringComparison.OrdinalIgnoreCase));
        if (nome is not null) return nome;

        // 3. Mesma posicao, e so com a mesma quantidade dos dois lados.
        return controles.Count == total ? controles[i] : null;
    }

    /// <summary>O "#2" de "Fan Control #2". Nulo quando o sensor nao e numerado.</summary>
    private static string? Sufixo(string nome)
    {
        var corte = nome.LastIndexOf('#');
        return corte >= 0 ? nome[corte..].Trim() : null;
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

    /// <summary>
    /// Tensao do nucleo. O sensor SVI2 fica travado num valor fixo em parte dos
    /// Ryzen, entao a preferencia e o maior VID entre os nucleos, que acompanha
    /// a carga. Vcore da placa-mae e a ultima opcao.
    /// </summary>
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

        return vids ?? Pegar(h => h == HardwareType.Cpu, SensorType.Voltage, "SVI2 TFN")
                    ?? Pegar(_ => true, SensorType.Voltage, "Vcore");
    }

    /// <summary>Taxas vem em bytes por segundo; o painel mostra em MB/s.</summary>
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

            // Descarta os limiares do fabricante e os sensores secundarios do
            // controlador, que sao valores fixos e altos.
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

                // Os mesmos limiares que a leitura de temperatura descarta.
                // Como temperatura atual enganam; como escala, vem do fabricante.
                Limiar(h, "Warning"),
                Limiar(h, "Critical")));
        }

        return lista;
    }

    /// <summary>
    /// Limiar termico publicado pela unidade. No NVMe sao o WCTEMP e o CCTEMP do
    /// proprio controlador; valores plausiveis so para nao deixar um sensor
    /// zerado ou absurdo virar escala.
    /// </summary>
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

        // Antes de fechar a biblioteca: depois dela, o comando de PWM nao chega
        // mais a placa, e uma ventoinha ficaria em 100% ate o proximo boot.
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
