using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>
/// Decide o valor maximo que serve de escala para cada medida, e de onde ele
/// veio.
///
/// A ordem e sempre a mesma: o que voce informou, depois o que a peca informa,
/// depois o que da para calcular dos proprios sensores, depois a ficha tecnica,
/// e so por ultimo o maior valor ja medido. Uma barra cheia pela metade passa a
/// significar metade do que a peca aguenta, e nao metade do que ela ja fez.
/// </summary>
public sealed class EspecificacaoService
{
    /// <summary>
    /// Abaixo deste PWM nao se projeta nada.
    ///
    /// A curva rotacao x PWM nao e uma reta: ha um minimo de partida embaixo e
    /// uma acomodacao em cima, e nas placas de video a porcentagem reportada nem
    /// sempre e o ciclo de trabalho bruto. Projetar de 40% para 100% multiplica
    /// o erro dessa curvatura por seis. De 60% para cima o trecho que falta e
    /// curto e quase reto, e a projecao para de pe.
    /// </summary>
    private const double PwmMinimoConfiavel = 60;

    /// <summary>
    /// Teto da projecao, como multiplo do que ja foi medido. Um ventilador nao
    /// dobra de rotacao do ponto de operacao normal ate o maximo; se a conta
    /// disser que dobra, a conta errou, e e melhor um teto conservador que um
    /// numero inventado.
    /// </summary>
    private const double FatorMaximoDaProjecao = 1.9;

    /// <summary>Abaixo disto o ponto nem entra na curva como referencia baixa.</summary>
    private const double PwmMinimoDaCurva = 20;

    /// <summary>Distancia minima entre os dois pontos para o ajuste de reta valer.</summary>
    private const double EspalhamentoMinimo = 15;

    /// <summary>Acima deste PWM a leitura ja e a rotacao maxima, sem conta nenhuma.</summary>
    private const double PwmCheio = 95;

    private readonly AppConfig _cfg;

    private bool _mudou;

    public CatalogoService Catalogo { get; }

    /// <summary>A ficha tecnica das pecas identificadas, se estiverem no catalogo.</summary>
    public Especificacao? Cpu { get; private set; }
    public Especificacao? Gpu { get; private set; }

    public EspecificacaoService(AppConfig cfg)
    {
        _cfg = cfg;
        Catalogo = new CatalogoService(cfg);
        Catalogo.Atualizado += () => Identificar(_nomeCpu, _nomeGpu);
    }

    private string? _nomeCpu;
    private string? _nomeGpu;

    /// <summary>Casa os nomes que os sensores reportam com as linhas do catalogo.</summary>
    public void Identificar(string? cpu, string? gpu)
    {
        _nomeCpu = cpu;
        _nomeGpu = gpu;
        Cpu = Catalogo.Cpu(cpu);
        Gpu = Catalogo.Gpu(gpu);
    }

    // ------------------------------------------------------------------
    // Ventoinhas: a rotacao maxima sai dos sensores, nao de tabela

    /// <summary>
    /// Registra um par rotacao/PWM. Guarda o ponto de maior e o de menor PWM ja
    /// vistos: com dois pontos da curva da para projetar onde ela chega a 100%,
    /// que e a rotacao maxima daquele ventilador — o que esta instalado, e nao o
    /// que uma tabela imagina.
    /// </summary>
    public void ObservarVentoinha(string chave, float rpm, float? pwm)
    {
        if (rpm <= 0) return;

        if (!_cfg.CurvasDeVentoinha.TryGetValue(chave, out var curva))
        {
            curva = new CurvaDeVentoinha();
            _cfg.CurvasDeVentoinha[chave] = curva;
            _mudou = true;
        }

        if (rpm > curva.RpmMax)
        {
            curva.RpmMax = rpm;
            _mudou = true;
        }

        if (pwm is not { } p || p < PwmMinimoDaCurva || p > 100) return;

        if (p > curva.PwmAlto)
        {
            curva.PwmAlto = p;
            curva.RpmAlto = rpm;
            _mudou = true;
        }

        if (curva.PwmBaixo <= 0 || p < curva.PwmBaixo)
        {
            curva.PwmBaixo = p;
            curva.RpmBaixo = rpm;
            _mudou = true;
        }
    }

    /// <summary>
    /// Rotacao maxima estimada de uma ventoinha.
    ///
    /// Com PWM em 95% ou mais, a leitura ja e a propria resposta. Com dois
    /// pontos distantes, a reta entre eles projetada ate 100% da o teto — e um
    /// ventilador tem um minimo de partida, entao a reta com intercepto acerta
    /// mais que uma regra de tres. Com um ponto so, resta a proporcao. Sem PWM
    /// nenhum, sobra o maior valor ja visto.
    /// </summary>
    public Teto TetoDeVentoinha(string chave, double? manual = null)
    {
        if (manual is > 0)
            return new Teto(manual.Value, Origem.Manual);

        if (!_cfg.CurvasDeVentoinha.TryGetValue(chave, out var c) || c.RpmMax <= 0)
            return Teto.Nenhum;

        // Calibrada, nao ha o que projetar: a rotacao maxima foi medida nela
        // mesma, com o PWM forcado a 100%.
        if (c.RpmCalibrado > 0)
            return new Teto(Math.Max(c.RpmCalibrado, c.RpmMax), Origem.Hardware,
                c.CalibradoEm is { } quando
                    ? $"Medido com a ventoinha a 100% em {quando:dd/MM/yyyy}."
                    : "Medido com a ventoinha a 100%.");

        if (c.PwmAlto >= PwmCheio)
            return new Teto(Math.Max(c.RpmAlto, c.RpmMax), Origem.Hardware,
                $"A ventoinha já girou a {c.PwmAlto:F0}% de PWM, então esta é a rotação real dela.");

        if (c.PwmAlto >= PwmMinimoConfiavel)
        {
            var espalhamento = c.PwmAlto - c.PwmBaixo;

            double estimado;
            string detalhe;

            if (espalhamento >= EspalhamentoMinimo && c.RpmAlto > c.RpmBaixo)
            {
                // Reta pelos dois pontos, projetada ate 100%.
                var inclinacao = (c.RpmAlto - c.RpmBaixo) / espalhamento;
                estimado = c.RpmAlto + inclinacao * (100 - c.PwmAlto);
                detalhe = $"Projetado de {c.RpmBaixo:F0} rpm a {c.PwmBaixo:F0}% e " +
                          $"{c.RpmAlto:F0} rpm a {c.PwmAlto:F0}% de PWM.";
            }
            else
            {
                estimado = c.RpmAlto / c.PwmAlto * 100;
                detalhe = $"Projetado de {c.RpmAlto:F0} rpm a {c.PwmAlto:F0}% de PWM. " +
                          "Fica mais exato quando a ventoinha passar por outras rotações.";
            }

            // Um ajuste ruim nao pode virar um teto absurdo, nem ficar abaixo do
            // que ja foi medido.
            estimado = Math.Clamp(estimado, c.RpmMax, c.RpmMax * FatorMaximoDaProjecao);

            return new Teto(Arredondar(estimado), Origem.Extrapolado,
                detalhe + "\nPara o número exato, calibre a ventoinha em Componentes.");
        }

        return new Teto(c.RpmMax, Origem.Medido, c.PwmAlto > 0
            ? $"A ventoinha ainda não passou de {c.PwmAlto:F0}% de PWM, e projetar de " +
              "tão baixo daria um número inventado. Calibrar em Componentes mede a " +
              "rotação máxima real em alguns segundos."
            : "A placa não reporta o PWM desta ventoinha, então não há curva para " +
              "projetar. Calibrar em Componentes mede a rotação máxima direto nela.");
    }

    /// <summary>
    /// Guarda a rotacao medida com o PWM em 100%. Vale mais que qualquer
    /// projecao, e por isso apaga os pontos da curva: eles existiam so para
    /// estimar o que agora foi medido.
    /// </summary>
    public void DefinirCalibrado(string chave, double rpm)
    {
        if (rpm <= 0) return;

        if (!_cfg.CurvasDeVentoinha.TryGetValue(chave, out var curva))
        {
            curva = new CurvaDeVentoinha();
            _cfg.CurvasDeVentoinha[chave] = curva;
        }

        curva.RpmCalibrado = rpm;
        curva.CalibradoEm = DateTime.Now;
        if (rpm > curva.RpmMax) curva.RpmMax = rpm;

        _mudou = true;
        _cfg.Salvar();
    }


    /// <summary>Rotacoes projetadas na casa da centena: a precisao nao daria para mais.</summary>
    private static double Arredondar(double rpm) => Math.Round(rpm / 50) * 50;

    // ------------------------------------------------------------------
    // Potencia

    public Teto TetoDePotenciaGpu() => Potencia(
        _cfg.PotenciaMaximaGpuW, Gpu?.PotenciaMaxW ?? 0, _cfg.PotenciaGpuMedida, "placa");

    public Teto TetoDePotenciaCpu() => Potencia(
        _cfg.PotenciaMaximaCpuW, Cpu?.PotenciaMaxW ?? 0, _cfg.PotenciaCpuMedida, "processador");

    /// <summary>
    /// Nem a GPU nem a CPU informam o proprio limite de potencia por sensor, so o
    /// consumo do instante. Entao o teto vem da ficha tecnica — e se o que ja foi
    /// medido passa dela, quem manda e a medicao: e o caso de uma placa de
    /// fabrica com limite elevado, em que a tabela do modelo de referencia
    /// subestima.
    /// </summary>
    private static Teto Potencia(int manual, double catalogo, double medido, string peca)
    {
        if (manual > 0) return new Teto(manual, Origem.Manual);

        if (catalogo > 0 && medido <= catalogo * 1.05)
            return new Teto(catalogo, Origem.Catalogo,
                $"Potência máxima de referência do modelo. Já medido: {medido:F0} W.");

        if (catalogo > 0 && medido > 0)
            return new Teto(medido, Origem.Medido,
                $"A {peca} já passou dos {catalogo:F0} W da ficha técnica, então a " +
                "escala segue a medição — é o que acontece com uma peça de fábrica " +
                "com limite elevado.");

        return medido > 0
            ? new Teto(medido, Origem.Medido)
            : Teto.Nenhum;
    }

    // ------------------------------------------------------------------
    // Temperatura

    /// <summary>
    /// Limite termico de um disco. O SMART publica os limiares de aviso e de
    /// critico do proprio fabricante — os mesmos valores que as abas descartam
    /// para nao confundi-los com a temperatura atual. Como escala, eles sao
    /// exatamente o numero certo.
    /// </summary>
    public Teto TetoDeDisco(float? critico, float? aviso, double configurado)
    {
        if (critico is > 0)
            return new Teto(critico.Value, Origem.Hardware,
                aviso is > 0 ? $"O fabricante avisa a partir de {aviso:F0} °C." : null);

        if (aviso is > 0)
            return new Teto(aviso.Value, Origem.Hardware,
                "Limiar de aviso do fabricante, publicado pelo SMART da unidade.");

        return new Teto(configurado, Origem.Manual,
            "O disco não publica limiar próprio, então vale o limite que você configurou.");
    }

    // ------------------------------------------------------------------
    // Taxas de disco

    /// <summary>
    /// Registra as taxas de uma unidade. Guardadas entre sessoes porque uma
    /// escala que recomeca do zero toda vez que o app abre transforma qualquer
    /// copia de arquivo na barra cheia.
    /// </summary>
    public void ObservarDisco(string nome, float? leitura, float? escrita)
    {
        Subir(_cfg.LeituraMaximaPorDisco, nome, leitura);
        Subir(_cfg.EscritaMaximaPorDisco, nome, escrita);
    }

    private void Subir(Dictionary<string, double> onde, string nome, float? taxa)
    {
        if (taxa is not { } t || t <= 0) return;
        if (t <= onde.GetValueOrDefault(nome)) return;

        onde[nome] = t;
        _mudou = true;
    }

    public Teto TetoDeLeitura(string nome) => TaxaDeDisco(_cfg.LeituraMaximaPorDisco, nome, "leitura");
    public Teto TetoDeEscrita(string nome) => TaxaDeDisco(_cfg.EscritaMaximaPorDisco, nome, "escrita");

    private static Teto TaxaDeDisco(Dictionary<string, double> onde, string nome, string qual)
    {
        var pico = onde.GetValueOrDefault(nome);

        return pico > 0
            ? new Teto(pico, Origem.Medido,
                $"Maior {qual} já registrada nesta unidade, mantida entre sessões. " +
                "A unidade não publica a velocidade de projeto dela, então esta é a " +
                "única referência vinda do próprio hardware.")
            : Teto.Nenhum;
    }

    // ------------------------------------------------------------------

    /// <summary>Limite termico da CPU ou da GPU, pelo TjMax da ficha tecnica.</summary>
    public Teto TetoTermicoCpu(double configurado) => Termico(Cpu?.TjMaxC ?? 0, configurado);
    public Teto TetoTermicoGpu(double configurado) => Termico(Gpu?.TjMaxC ?? 0, configurado);

    private static Teto Termico(double tjMax, double configurado) => tjMax > 0
        ? new Teto(tjMax, Origem.Catalogo, "Temperatura máxima de operação do modelo.")
        : new Teto(configurado, Origem.Manual, "Limite que você configurou.");

    /// <summary>Clock de boost da ficha tecnica, para a escala dos graficos de clock.</summary>
    public Teto TetoDeClockCpu(double medido) => Clock(Cpu?.ClockBoostMhz ?? 0, medido);
    public Teto TetoDeClockGpu(double medido) => Clock(Gpu?.ClockBoostMhz ?? 0, medido);

    private static Teto Clock(double boost, double medido)
    {
        if (boost > 0 && medido <= boost * 1.08)
            return new Teto(boost, Origem.Catalogo, "Clock de boost anunciado do modelo.");

        return medido > 0 ? new Teto(medido, Origem.Medido) : Teto.Nenhum;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Guarda o que foi aprendido. Chamado ao fechar o painel, nao a cada
    /// leitura: sao poucos bytes, mas gravar a cada segundo nao se justifica.
    /// </summary>
    public void Gravar()
    {
        if (!_mudou) return;

        _mudou = false;
        _cfg.Salvar();
    }

    public void MarcarMudanca() => _mudou = true;

    /// <summary>Esquece as curvas aprendidas, para o app remedir do zero.</summary>
    public void Esquecer()
    {
        _cfg.CurvasDeVentoinha.Clear();
        _cfg.PotenciaGpuMedida = 0;
        _cfg.PotenciaCpuMedida = 0;
        _cfg.RotacaoGpuMedida = 0;
        _cfg.Salvar();
    }
}
