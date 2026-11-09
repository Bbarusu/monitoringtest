using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed class EspecificacaoService
{
    private const double PwmMinimoConfiavel = 60;

    private const double FatorMaximoDaProjecao = 1.9;

    private const double PwmMinimoDaCurva = 20;

    private const double EspalhamentoMinimo = 15;

    private const double PwmCheio = 95;

    private readonly AppConfig _cfg;

    private bool _mudou;

    public CatalogoService Catalogo { get; }

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

    public void Identificar(string? cpu, string? gpu)
    {
        _nomeCpu = cpu;
        _nomeGpu = gpu;
        Cpu = Catalogo.Cpu(cpu);
        Gpu = Catalogo.Gpu(gpu);
    }

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

    public Teto TetoDeVentoinha(string chave, double? manual = null)
    {
        if (manual is > 0)
            return new Teto(manual.Value, Origem.Manual);

        if (!_cfg.CurvasDeVentoinha.TryGetValue(chave, out var c) || c.RpmMax <= 0)
            return Teto.Nenhum;

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

    private static double Arredondar(double rpm) => Math.Round(rpm / 50) * 50;

    public Teto TetoDePotenciaGpu() => Potencia(
        _cfg.PotenciaMaximaGpuW, Gpu?.PotenciaMaxW ?? 0, _cfg.PotenciaGpuMedida, "placa");

    public Teto TetoDePotenciaCpu() => Potencia(
        _cfg.PotenciaMaximaCpuW, Cpu?.PotenciaMaxW ?? 0, _cfg.PotenciaCpuMedida, "processador");

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

    public Teto TetoTermicoCpu(double configurado) => Termico(Cpu?.TjMaxC ?? 0, configurado);
    public Teto TetoTermicoGpu(double configurado) => Termico(Gpu?.TjMaxC ?? 0, configurado);

    private static Teto Termico(double tjMax, double configurado) => tjMax > 0
        ? new Teto(tjMax, Origem.Catalogo, "Temperatura máxima de operação do modelo.")
        : new Teto(configurado, Origem.Manual, "Limite que você configurou.");

    public Teto TetoDeClockCpu(double medido) => Clock(Cpu?.ClockBoostMhz ?? 0, medido);
    public Teto TetoDeClockGpu(double medido) => Clock(Gpu?.ClockBoostMhz ?? 0, medido);

    private static Teto Clock(double boost, double medido)
    {
        if (boost > 0 && medido <= boost * 1.08)
            return new Teto(boost, Origem.Catalogo, "Clock de boost anunciado do modelo.");

        return medido > 0 ? new Teto(medido, Origem.Medido) : Teto.Nenhum;
    }

    public void Gravar()
    {
        if (!_mudou) return;

        _mudou = false;
        _cfg.Salvar();
    }

    public void MarcarMudanca() => _mudou = true;

    public void Esquecer()
    {
        _cfg.CurvasDeVentoinha.Clear();
        _cfg.PotenciaGpuMedida = 0;
        _cfg.PotenciaCpuMedida = 0;
        _cfg.RotacaoGpuMedida = 0;
        _cfg.Salvar();
    }
}
