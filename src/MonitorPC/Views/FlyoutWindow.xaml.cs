using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class FlyoutWindow : Window
{
    private enum Aba { Geral, Cpu, Gpu, Disco, Energia }

    private const double Raio = 100;
    private const double RaioCpu = 69;

    /// <summary>5 minutos de historico a 1 s, que e o que o grafico mostra.</summary>
    private const int PontosDoHistorico = 300;

    private static readonly Color FundoSolido = Color.FromRgb(0x15, 0x17, 0x1C);

    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly AlertService _alertas;
    private readonly Queue<double> _historicoCpu = new();
    private readonly Queue<double> _historicoGpu = new();
    private readonly Queue<double> _historicoDisco = new();
    private readonly Queue<double> _historicoEnergia = new();
    private readonly DiskProcessService _processos = new();

    /// <summary>Quantas linhas a lista de processos reserva, para a altura nao oscilar.</summary>
    private const int LinhasDeProcesso = 3;

    /// <summary>-1 mostra o conjunto; acima disso, o indice da unidade escolhida.</summary>
    private int _discoEscolhido = -1;

    // Potencia e rotacao nao tem limite informado por sensor, entao a barra usa
    // como escala o maior valor visto desde que o app abriu.
    private double _gpuWattsMax;
    private double _gpuRpmMax;

    private Aba _aba = Aba.Geral;
    private bool _pausado;
    private bool _trocandoPerfil;
    private DateTime _recadoAte;
    private bool _posicionando;
    private bool _montandoSeletor;
    private Alerta? _alertaNaTela;

    public FlyoutWindow(AppConfig cfg, SensorService sensores, AlertService alertas)
    {
        InitializeComponent();
        _cfg = cfg;
        _sensores = sensores;
        _alertas = alertas;

        AplicarTransparencia();
        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() => Aplicar(snap));

        // O painel e ancorado pela base: crescendo, ele sobe, em vez de avancar
        // sobre a barra de tarefas. Vale para troca de aba e para qualquer
        // conteudo que mude de altura.
        SizeChanged += (_, _) => Posicionar();

        // Por padrao o painel fica aberto mesmo sem foco, para dar para olhar,
        // trocar de janela e tirar print. Quem fecha e o botao de minimizar
        // ou o icone da bandeja.
        Deactivated += (_, _) =>
        {
            if (_cfg.FecharAoPerderFoco) Esconder();
        };
    }

    // ------------------------------------------------------------------
    // Posicionamento e visibilidade

    /// <summary>
    /// Canto inferior direito da area de trabalho util, respeitando a barra de
    /// tarefas em qualquer lado da tela.
    /// </summary>
    private void Posicionar()
    {
        // UpdateLayout dispara SizeChanged, que chama este metodo de novo:
        // a guarda evita a recursao.
        if (_posicionando) return;
        _posicionando = true;

        try
        {
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
        }
        finally
        {
            _posicionando = false;
        }
    }

    public void Mostrar()
    {
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
        SincronizarPerfil();

        Opacity = 0;
        Show();
        Posicionar();
        Activate();

        _sensores.DefinirPainelAberto(true);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    public void Esconder()
    {
        _sensores.DefinirPainelAberto(false);

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }

    public void Alternar()
    {
        if (IsVisible) Esconder();
        else Mostrar();
    }

    /// <summary>
    /// A transparencia vai no alpha do fundo, nao na opacidade do elemento:
    /// Opacity desbota tambem o texto, os aneis e os icones, e o painel inteiro
    /// fica lavado. Com alpha so o fundo deixa passar o que esta atras.
    /// </summary>
    private void AplicarTransparencia()
    {
        Painel.Opacity = 1.0;

        if (!_cfg.Transparencia)
        {
            Painel.Background = new SolidColorBrush(FundoSolido);
            return;
        }

        var alpha = (byte)(Math.Clamp(_cfg.Opacidade, AppConfig.OpacidadeMinima, 100) * 255 / 100);
        Painel.Background = new SolidColorBrush(
            Color.FromArgb(alpha, FundoSolido.R, FundoSolido.G, FundoSolido.B));
    }

    // ------------------------------------------------------------------
    // Abas

    private void Aba_Selecionada(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        _aba = ReferenceEquals(sender, AbaCpu) ? Aba.Cpu
             : ReferenceEquals(sender, AbaGpu) ? Aba.Gpu
             : ReferenceEquals(sender, AbaDisco) ? Aba.Disco
             : ReferenceEquals(sender, AbaEnergia) ? Aba.Energia
             : Aba.Geral;

        // A medicao por processo so corre com a aba Disco aberta. Ao voltar, as
        // amostras antigas viram um pico falso, entao sao descartadas.
        _processos.Reiniciar();

        Mostrar(TopoGeral, BaseGeral, _aba == Aba.Geral);
        Mostrar(TopoCpu, BaseCpu, _aba == Aba.Cpu);
        Mostrar(TopoGpu, BaseGpu, _aba == Aba.Gpu);
        Mostrar(TopoDisco, BaseDisco, _aba == Aba.Disco);
        Mostrar(TopoEnergia, BaseEnergia, _aba == Aba.Energia);
        SeletorDisco.Visibility = _aba == Aba.Disco ? Visibility.Visible : Visibility.Collapsed;

        static void Mostrar(UIElement topo, UIElement baseP, bool visivel)
        {
            var v = visivel ? Visibility.Visible : Visibility.Collapsed;
            topo.Visibility = v;
            baseP.Visibility = v;
        }

        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);

        // A altura muda com a aba, entao o painel precisa se reancorar no canto.
        if (IsVisible) Posicionar();
    }

    private void SeletorDisco_Alterado(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _montandoSeletor) return;

        _discoEscolhido = SeletorDisco.SelectedIndex - 1;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    private void ModoDisco_Alterado(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    private void ModoGrade_Alterado(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    // ------------------------------------------------------------------
    // Dados

    private void Aplicar(Snapshot s)
    {
        AvisoDriver.Visibility = s.SensoresDeBaixoNivel ? Visibility.Collapsed : Visibility.Visible;

        MostrarAviso(_alertas.Atual);
        GuardarHistorico(s);

        switch (_aba)
        {
            case Aba.Cpu: AplicarCpu(s); break;
            case Aba.Gpu: AplicarGpu(s); break;
            case Aba.Disco: AplicarDisco(s); break;
            case Aba.Energia: AplicarEnergia(s); break;
            default: AplicarGeral(s); break;
        }

        AtualizarStatus(s);
    }

    private void GuardarHistorico(Snapshot s)
    {
        Guardar(_historicoCpu, s.CpuUso);
        Guardar(_historicoGpu, s.GpuUso);
        Guardar(_historicoDisco, s.DiscoAtividade);

        // O grafico trabalha em porcentagem, entao a potencia entra como fracao
        // da capacidade da fonte.
        if (_cfg.CapacidadeFonteW > 0)
            Guardar(_historicoEnergia, (float)(s.WattsTotais / _cfg.CapacidadeFonteW * 100));

        if (s.GpuWatts is { } w) _gpuWattsMax = Math.Max(_gpuWattsMax, w);
        if (s.GpuFanRpm is { } r) _gpuRpmMax = Math.Max(_gpuRpmMax, r);

        static void Guardar(Queue<double> fila, float? valor)
        {
            if (valor is not { } v) return;
            fila.Enqueue(Math.Clamp(v, 0, 100));
            while (fila.Count > PontosDoHistorico) fila.Dequeue();
        }
    }

    private void AplicarGeral(Snapshot s)
    {
        Titulo.Text = "Meu PC";
        Chips(Icone.Termometro, s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C",
              Icone.Memoria, s.RamUso is { } r ? $"{r:F0}%" : "--");

        ValorPrincipal.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0}" : "--";
        LegendaPrincipal.Text = s.CpuUso is { } uso ? $"CPU · {uso:F0}% de carga" : "CPU";

        var carga = Math.Clamp(s.CpuUso ?? 0, 0, 100);
        Anel.Data = ArcoDeCarga(carga, Raio, 110);
        ColunaCheia.Width = new GridLength(carga, GridUnitType.Star);
        ColunaVazia.Width = new GridLength(100 - carga, GridUnitType.Star);

        // Passar do limite configurado pinta o anel e o numero de laranja.
        var quente = s.CpuTemp is { } t && t >= _cfg.LimiteCpu;
        var cor = quente ? Pincel("Alerta") : Pincel("Acento");
        Anel.Stroke = cor;
        BarraCarga.Background = cor;
        ValorPrincipal.Foreground = quente ? Pincel("AlertaTexto") : Brushes.White;

        CardGpu.Text = s.GpuTemp is { } gt && s.GpuUso is { } gu ? $"{gt:F0}° · {gu:F0}%" : "--";
        CardRam.Text = s.RamUsadaGb is { } usada && s.RamTotalGb is { } total
            ? $"{usada:F1} / {total:F0} GB"
            : s.RamUso is { } ru ? $"{ru:F0}% em uso" : "--";
    }

    private void AplicarCpu(Snapshot s)
    {
        Titulo.Text = "CPU";
        Chips(Icone.Termometro, s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C",
              Icone.Relogio, s.CpuClockMhz is > 0 ? Clock(s.CpuClockMhz!.Value) : "-- GHz");

        var uso = Math.Clamp(s.CpuUso ?? 0, 0, 100);
        CpuUsoTexto.Text = s.CpuUso is null ? "--" : $"{uso:F0}";
        AnelCpu.Data = ArcoDeCarga(uso, RaioCpu, 75);

        var quente = s.CpuTemp is { } t && t >= _cfg.LimiteCpu;
        AnelCpu.Stroke = quente ? Pincel("Alerta") : Pincel("Acento");

        CpuTempTexto.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "n/d";
        CpuTempTexto.Foreground = quente ? Pincel("AlertaTexto") : Pincel("Texto");
        CpuClockTexto.Text = s.CpuClockMhz is > 0 ? Clock(s.CpuClockMhz!.Value) : "n/d";
        CpuWattsTexto.Text = s.CpuWatts is > 0 ? $"{s.CpuWatts:F0} W" : "n/d";
        CpuTensaoTexto.Text = s.CpuTensao is > 0 ? $"{s.CpuTensao:0.00} V" : "n/d";

        DesenharHistorico();
        MontarGrade(s);
    }

    private void AplicarGpu(Snapshot s)
    {
        Titulo.Text = s.GpuNome ?? "GPU";
        Chips(Icone.Termometro, s.GpuTemp is > 0 ? $"{s.GpuTemp:F0} °C" : "-- °C",
              Icone.Raio, s.GpuWatts is > 0 ? $"{s.GpuWatts:F0} W" : "-- W");

        var uso = Math.Clamp(s.GpuUso ?? 0, 0, 100);
        GpuUsoTexto.Text = s.GpuUso is null ? "--" : $"{uso:F0}";
        AnelGpu.Data = ArcoDeCarga(uso, RaioCpu, 75);

        var quente = s.GpuTemp is { } t && t >= _cfg.LimiteGpu;
        AnelGpu.Stroke = quente ? Pincel("Alerta") : Pincel("Acento");

        GpuTempTexto.Text = s.GpuTemp is > 0 ? $"{s.GpuTemp:F0} °C" : "n/d";
        GpuTempTexto.Foreground = quente ? Pincel("AlertaTexto") : Pincel("Texto");
        GpuClockTexto.Text = s.GpuClockNucleo is > 0 ? Clock(s.GpuClockNucleo!.Value) : "n/d";
        GpuClockMemTexto.Text = s.GpuClockMemoria is > 0 ? Clock(s.GpuClockMemoria!.Value) : "n/d";

        AplicarHotSpot(s);
        DesenharHistoricoGpu();
        MontarBarrasGpu(s);
    }

    /// <summary>
    /// Uma diferenca grande entre o hot spot e a borda costuma indicar contato
    /// ruim do dissipador ou pasta termica ressecada.
    /// </summary>
    private void AplicarHotSpot(Snapshot s)
    {
        if (s.GpuHotSpot is not { } hot || hot <= 0)
        {
            GpuHotTexto.Text = "n/d";
            GpuHotTexto.Foreground = Pincel("Texto");
            AvisoHotSpot.Visibility = Visibility.Collapsed;
            return;
        }

        var delta = s.GpuTemp is { } borda && borda > 0 ? hot - borda : (float?)null;
        var preocupante = delta >= 20;

        GpuHotTexto.Text = delta is { } d ? $"{hot:F0} °C  Δ{d:F0}" : $"{hot:F0} °C";
        GpuHotTexto.Foreground = preocupante ? Pincel("AlertaTexto") : Pincel("Texto");

        AvisoHotSpot.Visibility = preocupante ? Visibility.Visible : Visibility.Collapsed;
        if (preocupante)
            TextoHotSpot.Text = $"Hot spot {delta:F0} °C acima da borda. " +
                                "Pode indicar pasta térmica ressecada.";
    }

    /// <summary>
    /// VRAM tem total informado pelo sensor. Potencia e rotacao nao tem limite
    /// publicado, entao a escala e o maior valor visto nesta sessao, o que o
    /// tooltip deixa explicito.
    /// </summary>
    private void MontarBarrasGpu(Snapshot s)
    {
        var barras = new List<(string Rotulo, string Valor, double Pct, string? Dica)>();

        if (s.VramUsadaMb is { } usada && s.VramTotalMb is { } total && total > 0)
            barras.Add(("VRAM",
                $"{usada / 1024:0.0} / {total / 1024:0.0} GB",
                usada / total * 100,
                null));

        if (s.GpuWatts is { } w && w > 0)
            barras.Add(("Potência",
                $"{w:F0} W",
                _gpuWattsMax > 0 ? w / _gpuWattsMax * 100 : 0,
                $"Escala: maior valor visto nesta sessão ({_gpuWattsMax:F0} W)"));

        if (s.GpuFanRpm is { } rpm)
            barras.Add(("Ventilador",
                rpm > 0 ? $"{rpm:F0} rpm" : "parado",
                _gpuRpmMax > 0 ? rpm / _gpuRpmMax * 100 : 0,
                _gpuRpmMax > 0
                    ? $"Escala: maior valor visto nesta sessão ({_gpuRpmMax:F0} rpm)"
                    : "A ventoinha só liga sob carga"));

        if (s.GpuTempMemoria is { } tm && tm > 0)
            barras.Add(("Temp. memória", $"{tm:F0} °C", Math.Clamp(tm / 110 * 100, 0, 100),
                "Escala até 110 °C, o limite típico de GDDR"));

        while (BarrasGpu.Children.Count > barras.Count)
            BarrasGpu.Children.RemoveAt(BarrasGpu.Children.Count - 1);
        while (BarrasGpu.Children.Count < barras.Count)
            BarrasGpu.Children.Add(new BarraDeMetrica());

        for (var i = 0; i < barras.Count; i++)
        {
            var (rotulo, valor, pct, dica) = barras[i];
            ((BarraDeMetrica)BarrasGpu.Children[i]).Atualizar(rotulo, valor, pct, dica);
        }
    }

    private void AplicarDisco(Snapshot s)
    {
        MontarSeletor(s);

        // -1 e o conjunto; acima disso, uma unidade so.
        var escolhido = _discoEscolhido >= 0 && _discoEscolhido < s.Discos.Count
            ? s.Discos[_discoEscolhido]
            : null;

        var atividade = Math.Clamp(escolhido?.Atividade ?? s.DiscoAtividade ?? 0, 0, 100);
        // Com uma unidade escolhida, a temperatura e dela. No conjunto, a da
        // primeira unidade — a do sistema —, nao a mais alta das tres.
        var temperatura = escolhido?.TemperaturaC
                       ?? s.Discos.FirstOrDefault()?.TemperaturaC
                       ?? s.DiscoTempMaior;
        var vida = escolhido?.VidaPct ?? s.DiscoVidaMenor;
        var leitura = escolhido?.LeituraMbs ?? s.DiscoLeituraMbs;
        var escrita = escolhido?.EscritaMbs ?? s.DiscoEscritaMbs;

        Titulo.Text = escolhido is not null
            ? "Disco"
            : s.Discos.Count == 1 ? "Disco" : $"Discos · {s.Discos.Count} unidades";

        Chips(Icone.Termometro, temperatura is > 0 ? $"{temperatura:F0} °C" : "-- °C",
              Icone.Escudo, vida is { } v ? $"Vida {v:F0}%" : "Vida --");

        DiscoUsoTexto.Text = escolhido?.Atividade is null && s.DiscoAtividade is null
            ? "--"
            : $"{atividade:F0}";
        AnelDisco.Data = ArcoDeCarga(atividade, RaioCpu, 75);

        // O alerta olha a mais alta entre as unidades: um disco quente nao pode
        // passar despercebido so porque o painel mostra outro.
        var maisAlta = escolhido?.TemperaturaC ?? s.DiscoTempMaior;
        var quente = maisAlta is { } t && t >= _cfg.LimiteDisco;
        AnelDisco.Stroke = quente ? Pincel("Alerta") : Pincel("Acento");

        DiscoLeituraTexto.Text = Taxa(leitura);
        DiscoEscritaTexto.Text = Taxa(escrita);
        DiscoTempTexto.Text = temperatura is > 0 ? $"{temperatura:F0} °C" : "n/d";
        DiscoTempTexto.Foreground = quente ? Pincel("AlertaTexto") : Pincel("Texto");
        DiscoTempTexto.ToolTip = escolhido is null && s.Discos.Count > 1
            ? string.Join("\n", s.Discos
                .Where(d => d.TemperaturaC is > 0)
                .Select(d => $"{d.Nome}: {d.TemperaturaC:F0} °C"))
            : null;

        // Numa unidade so, as horas ligado cabem ao lado da vida util.
        DiscoVidaTexto.Text = escolhido is { VidaPct: { } vp, HorasLigado: { } h }
            ? $"{vp:F0}% · {h:N0} h"
            : vida is { } vm ? $"{vm:F0}%" : "n/d";

        // O historico e sempre do conjunto: trocar de unidade nao apaga o grafico.
        DesenharHistoricoDisco();

        if (ModoUnidades.IsChecked == true) MontarUnidades(s);
        else MontarProcessos();
    }

    /// <summary>
    /// A lista do seletor so e refeita quando as unidades mudam, para nao
    /// descartar a escolha do usuario a cada leitura.
    /// </summary>
    private void MontarSeletor(Snapshot s)
    {
        var desejado = new List<string> { s.Discos.Count == 1 ? "a unidade" : "todas" };
        desejado.AddRange(s.Discos.Select(d => Encurtar(d.Nome)));

        var atual = SeletorDisco.Items.Cast<object>().Select(x => x?.ToString()).ToList();
        if (atual.SequenceEqual(desejado)) return;

        _montandoSeletor = true;
        try
        {
            SeletorDisco.Items.Clear();
            foreach (var item in desejado) SeletorDisco.Items.Add(item);

            if (_discoEscolhido >= s.Discos.Count) _discoEscolhido = -1;
            SeletorDisco.SelectedIndex = _discoEscolhido + 1;
        }
        finally
        {
            _montandoSeletor = false;
        }
    }

    /// <summary>Nomes de modelo sao longos demais para o cabecalho.</summary>
    private static string Encurtar(string nome) =>
        nome.Length <= 22 ? nome : nome[..21].TrimEnd() + "…";

    /// <summary>
    /// As taxas de disco em repouso ficam na casa dos KB/s. Arredondar tudo para
    /// MB/s escondia o movimento real, entao a unidade acompanha a grandeza.
    /// </summary>
    private static string Taxa(float? mbs)
    {
        if (mbs is not { } v) return "n/d";
        if (v <= 0) return "0";
        return v < 1 ? $"{v * 1024:0} KB/s" : $"{v:0.0} MB/s";
    }

    /// <summary>Uma linha por unidade, com o que o SMART informa.</summary>
    private void MontarUnidades(Snapshot s)
    {
        RotuloListaDisco.Text = "Unidades";

        SincronizarLinhas(s.Discos.Count);

        for (var i = 0; i < s.Discos.Count; i++)
        {
            var d = s.Discos[i];

            var detalhe = new List<string>();
            if (d.TemperaturaC is > 0) detalhe.Add($"{d.TemperaturaC:F0} °C");
            if (d.VidaPct is { } vida) detalhe.Add($"vida {vida:F0}%");
            if (d.HorasLigado is { } horas) detalhe.Add($"{horas:N0} h");

            var espaco = d.TotalGb is { } total && total > 0 && d.UsadoPct is { } usado
                ? $"{total * usado / 100:N0} / {total:N0} GB"
                : d.TotalGb is { } t ? $"{t:N0} GB" : "";

            ((LinhaDeDisco)ListaDisco.Children[i]).Atualizar(
                d.Nome,
                string.Join(" · ", detalhe),
                espaco,
                d.UsadoPct ?? 0);
        }
    }

    /// <summary>
    /// Os processos que mais usam disco agora. A medicao so corre com esta aba
    /// aberta, para nao varrer a lista de processos o tempo todo.
    ///
    /// As linhas sao sempre as mesmas: com a quantidade variando, o painel
    /// mudava de altura a cada leitura.
    /// </summary>
    private void MontarProcessos()
    {
        RotuloListaDisco.Text = "Consumindo o disco agora";

        var processos = _processos.Medir(LinhasDeProcesso);
        var maior = processos.Count > 0 ? processos.Max(p => p.TotalMbs) : 0;

        SincronizarLinhas(LinhasDeProcesso);

        for (var i = 0; i < LinhasDeProcesso; i++)
        {
            var linha = (LinhaDeDisco)ListaDisco.Children[i];

            if (i >= processos.Count)
            {
                linha.Vazia(i == 0 ? "Nenhum processo lendo ou escrevendo" : "");
                continue;
            }

            var p = processos[i];
            linha.Atualizar(
                p.Nome,
                $"PID {p.Pid} · leitura {p.LeituraMbs:0.0} · escrita {p.EscritaMbs:0.0} MB/s",
                $"{p.TotalMbs:0.0} MB/s",
                maior > 0 ? p.TotalMbs / maior * 100 : 0);
        }
    }

    private void SincronizarLinhas(int quantidade)
    {
        while (ListaDisco.Children.Count > quantidade)
            ListaDisco.Children.RemoveAt(ListaDisco.Children.Count - 1);
        while (ListaDisco.Children.Count < quantidade)
            ListaDisco.Children.Add(new LinhaDeDisco());
    }

    private void AplicarEnergia(Snapshot s)
    {
        Titulo.Text = "Energia";
        Chips(Icone.Raio, $"{s.WattsTotais:F0} W",
              Icone.Relogio, $"{Kwh(s.WhAcumulado)} na sessão");

        EnergiaWattsTexto.Text = $"{s.WattsTotais:F0}";

        // O anel compara a potencia com a capacidade da fonte.
        var fracao = _cfg.CapacidadeFonteW > 0
            ? s.WattsTotais / _cfg.CapacidadeFonteW * 100
            : 0;
        AnelEnergia.Data = ArcoDeCarga(fracao, RaioCpu, 75);
        EnergiaLegenda.Text = _cfg.CapacidadeFonteW > 0
            ? $"de {_cfg.CapacidadeFonteW} W"
            : "Potência agora";

        EnergiaSessaoTexto.Text = Kwh(s.WhAcumulado);
        EnergiaMediaTexto.Text = $"{s.WattsMedia:F0} W";
        EnergiaPicoTexto.Text = $"{s.WattsPico:F0} W";
        EnergiaTomadaTexto.Text = _cfg.EficienciaFonte > 0
            ? $"{s.WattsTotais / _cfg.EficienciaFonte:F0} W"
            : "n/d";

        DesenharHistoricoEnergia();
        MontarDistribuicao(s);
    }

    /// <summary>
    /// Separa o que e medido do que e estimado: so CPU e GPU tem sensor de
    /// potencia; o resto vem das quantidades detectadas e dos watts por unidade.
    /// </summary>
    private void MontarDistribuicao(Snapshot s)
    {
        var gpu = s.GpuWatts ?? 0;
        var cpu = s.CpuWatts ?? 0;
        var outros = s.WattsEstimados;
        var total = Math.Max(gpu + cpu + outros, 0.001);

        FaixaGpu.Width = new GridLength(gpu / total * 100, GridUnitType.Star);
        FaixaCpu.Width = new GridLength(cpu / total * 100, GridUnitType.Star);
        FaixaOutros.Width = new GridLength(outros / total * 100, GridUnitType.Star);

        var itens = new (string Cor, string Nome, double Watts, string Origem)[]
        {
            ("Acento", "GPU", gpu, "medido"),
            ("#8FBAFF", "CPU", cpu, "medido"),
            ("#4B5365", DescricaoDosOutros(s), outros, "estimado")
        };

        while (LegendaEnergia.Children.Count > itens.Length)
            LegendaEnergia.Children.RemoveAt(LegendaEnergia.Children.Count - 1);
        while (LegendaEnergia.Children.Count < itens.Length)
            LegendaEnergia.Children.Add(new LinhaDeLegenda());

        for (var i = 0; i < itens.Length; i++)
        {
            var (cor, nome, watts, origem) = itens[i];
            ((LinhaDeLegenda)LegendaEnergia.Children[i])
                .Atualizar(cor, nome, $"{watts:F0} W", origem);
        }

        NotaEnergia.Text = "Energia (Wh) = potência × tempo, somada a cada leitura. " +
                           "Ajuste as quantidades e os watts por unidade no config.json.";
    }

    /// <summary>
    /// Sempre em kWh, que e a unidade da conta de luz. Nos primeiros minutos o
    /// valor e pequeno, entao as casas decimais acompanham a grandeza.
    /// </summary>
    private static string Kwh(double wh)
    {
        var kwh = wh / 1000.0;
        return kwh < 0.01 ? $"{kwh:0.000} kWh" : $"{kwh:0.00} kWh";
    }

    private static string DescricaoDosOutros(Snapshot s)
    {
        var partes = new List<string>();
        if (s.ModulosRam > 0) partes.Add($"{s.ModulosRam} de RAM");
        if (s.Discos2 > 0) partes.Add($"{s.Discos2} discos");
        if (s.VentoinhasAtivas > 0) partes.Add($"{s.VentoinhasAtivas} ventoinhas");

        return partes.Count == 0 ? "Outros componentes" : string.Join(", ", partes) + " e outros";
    }

    /// <summary>
    /// O aviso fica acima das abas, entao vale em qualquer uma: uma temperatura
    /// alta nao deve depender de o usuario estar na aba certa para aparecer.
    /// </summary>
    private void MostrarAviso(Alerta? alerta)
    {
        _alertaNaTela = alerta;

        if (alerta is null)
        {
            AvisoTemperatura.Visibility = Visibility.Collapsed;
            return;
        }

        AvisoTitulo.Text = alerta.Titulo;
        AvisoDetalhe.Text = alerta.Detalhe;

        // O botao de Economico so aparece se a pessoa pediu essa sugestao, e nao
        // faz sentido quando o plano ja e esse.
        var oferecer = _cfg.SugerirEconomico && PowerService.Atual() != PerfilEnergia.Economico;
        BotaoEconomico.Visibility = oferecer ? Visibility.Visible : Visibility.Collapsed;

        AvisoTemperatura.Visibility = Visibility.Visible;
    }

    private void AvisoEconomico_Click(object sender, RoutedEventArgs e)
    {
        if (PowerService.Aplicar(PerfilEnergia.Economico))
        {
            Recado("Plano de energia: Econômico");
            SincronizarPerfil();
            BotaoEconomico.Visibility = Visibility.Collapsed;
            return;
        }

        Recado("Não foi possível trocar o plano de energia");
    }

    /// <summary>
    /// Silencia so o componente do aviso em tela: silenciar o disco nao pode
    /// esconder um aviso da CPU, que e outro problema.
    /// </summary>
    private void AvisoSilenciar_Click(object sender, RoutedEventArgs e)
    {
        if (_alertaNaTela is not { } alerta) return;

        _alertas.Silenciar(alerta.Componente);
        AvisoTemperatura.Visibility = Visibility.Collapsed;
        Recado($"Aviso de {alerta.Componente} silenciado por 10 minutos");

        // Outro componente pode estar acima do limite e assumir o aviso agora.
        if (_sensores.Ultimo is { } ultimo)
        {
            _alertas.Avaliar(ultimo);
            MostrarAviso(_alertas.Atual);
        }
    }

    private void AtualizarStatus(Snapshot s)
    {
        // Um recado recente sobre o plano de energia segura o rodape por alguns
        // segundos: senao a proxima leitura o apagaria antes de dar para ler.
        if (DateTime.UtcNow < _recadoAte) return;

        var intervalo = _pausado ? "pausado" : "atualiza a cada 1 s";
        Status.Text = s.SensoresDeBaixoNivel
            ? $"Sensores ativos · {intervalo}"
            : $"Sensores parciais · {intervalo}";
    }

    private static string Clock(float mhz) =>
        mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:F0} MHz";

    private static Brush Pincel(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>Desenhos dos icones dos chips, num quadro de 24 por 24.</summary>
    private static class Icone
    {
        public const string Termometro = "M14,14.8 L14,4 A2,2 0 1,0 10,4 L10,14.8 A4,4 0 1,0 14,14.8 Z";
        public const string Memoria = "M3,8 L21,8 L21,16 L3,16 Z M7,16 L7,19 M12,16 L12,19 M17,16 L17,19";
        public const string Raio = "M13,2 L4,14 L11,14 L10,22 L19,10 L12,10 Z";
        public const string Relogio = "M12,3 A9,9 0 1,0 12,21 A9,9 0 1,0 12,3 Z M12,7 L12,12 L15,14";
        public const string Disco = "M3,6 L21,6 L21,18 L3,18 Z M7,14 L11,14";
        public const string Escudo = "M12,3 L20,6 L20,12 C20,17 16.5,20 12,21 C7.5,20 4,17 4,12 L4,6 Z";
    }

    /// <summary>Preenche os dois chips do cabecalho; sem desenho, o icone some.</summary>
    private void Chips(string? iconeEsq, string textoEsq, string? iconeDir, string textoDir)
    {
        Aplicar(IconeEsquerdo, iconeEsq);
        Aplicar(IconeDireito, iconeDir);
        ChipEsquerdo.Text = textoEsq;
        ChipDireito.Text = textoDir;

        static void Aplicar(Path alvo, string? desenho)
        {
            if (desenho is null)
            {
                alvo.Visibility = Visibility.Collapsed;
                return;
            }

            alvo.Data = Geometry.Parse(desenho);
            alvo.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------
    // Grade de nucleos

    /// <summary>
    /// A contagem vem do hardware, nunca fixa. Em "Núcleos" cada barra e a media
    /// das threads daquele nucleo, que e como a carga se distribui num CPU com
    /// duas threads por nucleo; em "Threads" cada barra e uma thread.
    /// </summary>
    private void MontarGrade(Snapshot s)
    {
        var threads = s.CpuCargaPorThread;
        var clocks = s.CpuClocksPorNucleo;

        var porNucleo = ModoNucleos.IsChecked == true;
        var podeAgrupar = clocks.Count > 0 && threads.Count == clocks.Count * 2;

        List<double> valores;
        string prefixo;

        if (porNucleo && podeAgrupar)
        {
            valores = new List<double>(clocks.Count);
            for (var i = 0; i < clocks.Count; i++)
                valores.Add((threads[i * 2] + threads[i * 2 + 1]) / 2.0);
            prefixo = "N";
        }
        else if (porNucleo && clocks.Count > 0 && threads.Count == clocks.Count)
        {
            // Sem SMT: thread e nucleo sao a mesma coisa.
            valores = threads.Select(v => (double)v).ToList();
            prefixo = "N";
        }
        else
        {
            valores = threads.Select(v => (double)v).ToList();
            prefixo = "T";
        }

        RotuloGrade.Text = prefixo == "N" ? "Uso por núcleo" : "Uso por thread";
        DetectadoCpu.Text = clocks.Count > 0
            ? $"detectado: {clocks.Count} núcleos · {threads.Count} threads"
            : $"detectado: {threads.Count} threads";

        // Sem agrupamento possivel, a opcao "Núcleos" nao teria o que mostrar.
        ModoNucleos.IsEnabled = clocks.Count > 0;

        SincronizarBarras(valores.Count);

        for (var i = 0; i < valores.Count; i++)
        {
            var linha = (BarraDeNucleo)GradeNucleos.Children[i];
            var clock = prefixo == "N" && i < clocks.Count ? clocks[i] : (float?)null;

            // O tooltip diz de onde veio o numero, para dar para conferir a
            // media contra o Gerenciador de Tarefas.
            var origem = prefixo == "N" && podeAgrupar
                ? $"média de T{i * 2 + 1} e T{i * 2 + 2}"
                : null;

            linha.Atualizar($"{prefixo}{i + 1}", valores[i], clock, origem);
        }
    }

    /// <summary>Cria ou remove barras so quando a contagem muda.</summary>
    private void SincronizarBarras(int quantidade)
    {
        while (GradeNucleos.Children.Count > quantidade)
            GradeNucleos.Children.RemoveAt(GradeNucleos.Children.Count - 1);

        while (GradeNucleos.Children.Count < quantidade)
            GradeNucleos.Children.Add(new BarraDeNucleo());

        // Sem folga entre as duas colunas a porcentagem de uma encosta no
        // rotulo da seguinte.
        for (var i = 0; i < GradeNucleos.Children.Count; i++)
        {
            var par = i % 2 == 0;
            ((BarraDeNucleo)GradeNucleos.Children[i]).Margin =
                new Thickness(0, 0, par ? 18 : 0, 8);
        }
    }

    // ------------------------------------------------------------------
    // Grafico do historico

    private void AreaGrafico_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistorico();

    private void AreaGraficoGpu_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistoricoGpu();

    private void AreaGraficoDisco_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistoricoDisco();

    private void AreaGraficoEnergia_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistoricoEnergia();

    private void DesenharHistorico() =>
        Desenhar(_historicoCpu, AreaGrafico, GraficoLinha, GraficoArea, CpuPicoHistorico);

    private void DesenharHistoricoGpu() =>
        Desenhar(_historicoGpu, AreaGraficoGpu, GraficoLinhaGpu, GraficoAreaGpu, GpuPicoHistorico);

    private void DesenharHistoricoDisco() =>
        Desenhar(_historicoDisco, AreaGraficoDisco, GraficoLinhaDisco, GraficoAreaDisco, DiscoPicoHistorico);

    private void DesenharHistoricoEnergia()
    {
        Desenhar(_historicoEnergia, AreaGraficoEnergia, GraficoLinhaEnergia, GraficoAreaEnergia,
                 EnergiaPicoHistorico);

        // O historico esta em porcentagem da fonte; o rotulo mostra watts.
        if (_historicoEnergia.Count >= 2)
            EnergiaPicoHistorico.Text =
                $"Pico: {_historicoEnergia.Max() / 100 * _cfg.CapacidadeFonteW:F0} W";
    }

    private static void Desenhar(Queue<double> historico, FrameworkElement area,
        Polyline linha, Polygon preenchimento, TextBlock rotuloDoPico)
    {
        var largura = area.ActualWidth;
        var altura = area.ActualHeight;

        if (largura <= 0 || altura <= 0 || historico.Count < 2)
        {
            linha.Points = new PointCollection();
            preenchimento.Points = new PointCollection();
            rotuloDoPico.Text = "";
            return;
        }

        var valores = historico.ToArray();

        // O grafico ocupa sempre a largura toda: com poucos pontos ele cresce da
        // esquerda para a direita conforme o historico enche.
        var passo = largura / Math.Max(valores.Length - 1, 1);

        var pontos = new PointCollection(valores.Length);
        for (var i = 0; i < valores.Length; i++)
        {
            var y = altura - valores[i] / 100.0 * (altura - 4) - 2;
            pontos.Add(new Point(i * passo, y));
        }

        linha.Points = pontos;

        // A area e a mesma linha fechada na base.
        preenchimento.Points = new PointCollection(pontos) { new(largura, altura), new(0, altura) };

        rotuloDoPico.Text = $"Pico: {valores.Max():F0}%";
    }

    // ------------------------------------------------------------------
    // Anel

    /// <summary>Arco do topo no sentido horario, proporcional a carga.</summary>
    private static Geometry? ArcoDeCarga(double porcentagem, double raio, double centro)
    {
        // Abaixo de uns 2% o arco vira um ponto solto no topo. Um minimo visual
        // mantem a leitura de "tem pouca carga" sem parecer defeito.
        var valor = Math.Clamp(porcentagem, 0, 100);
        if (valor <= 0) return null;
        valor = Math.Max(valor, 1.8);

        var angulo = Math.Min(valor, 99.999) / 100.0 * 2 * Math.PI;
        var meio = new Point(centro, centro);
        var inicio = new Point(meio.X, meio.Y - raio);
        var fim = new Point(
            meio.X + raio * Math.Sin(angulo),
            meio.Y - raio * Math.Cos(angulo));

        var arco = new ArcSegment(fim, new Size(raio, raio), 0,
            angulo > Math.PI, SweepDirection.Clockwise, true);

        return new PathGeometry(new[] { new PathFigure(inicio, new[] { arco }, false) });
    }

    // ------------------------------------------------------------------
    // Perfis de energia

    /// <summary>
    /// O plano pode ter sido trocado fora do app, entao o painel le o que
    /// esta em vigor em vez de assumir o que ele mesmo selecionou.
    /// </summary>
    private void SincronizarPerfil()
    {
        var atual = PowerService.Atual();

        _trocandoPerfil = true;
        PerfilEconomico.IsChecked = atual == PerfilEnergia.Economico;
        PerfilEquilibrado.IsChecked = atual == PerfilEnergia.Equilibrado;
        PerfilDesempenho.IsChecked = atual == PerfilEnergia.Desempenho;
        _trocandoPerfil = false;
    }

    private void Perfil_Click(object sender, RoutedEventArgs e)
    {
        if (_trocandoPerfil) return;

        var perfil = sender switch
        {
            var s when ReferenceEquals(s, PerfilEconomico) => PerfilEnergia.Economico,
            var s when ReferenceEquals(s, PerfilDesempenho) => PerfilEnergia.Desempenho,
            _ => PerfilEnergia.Equilibrado
        };

        // O Desempenho nao e so um plano de energia: abre a tela com as demais
        // acoes, para a pessoa escolher o que fazer.
        if (perfil == PerfilEnergia.Desempenho)
        {
            AbrirDesempenho();
            return;
        }

        if (PowerService.Aplicar(perfil))
        {
            Recado($"Plano de energia: {perfil}");
            return;
        }

        // Falhou: volta a marcacao para o plano que realmente esta ativo,
        // em vez de deixar o painel mostrando algo que nao vigora.
        Recado("Não foi possível trocar o plano de energia");
        SincronizarPerfil();
    }

    private void Recado(string texto)
    {
        Status.Text = texto;
        _recadoAte = DateTime.UtcNow.AddSeconds(4);
    }

    // ------------------------------------------------------------------
    // Botoes

    private void Minimizar_Click(object sender, RoutedEventArgs e) => Esconder();

    private void Personalizar_Click(object sender, RoutedEventArgs e)
    {
        // A tela aplica cor e transparencia na hora, para dar para ver o efeito
        // enquanto se mexe nos controles.
        var tela = new PersonalizacaoWindow(_cfg, () =>
        {
            (Application.Current as App)?.AplicarAparencia();
            AplicarTransparencia();
        })
        { Owner = this };

        tela.ShowDialog();
    }

    private void Ajustes_Click(object sender, RoutedEventArgs e) => AbrirConfiguracao(false);

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        new LogDiscoWindow(_cfg) { Owner = this }.ShowDialog();

        // O log usa a mesma medicao por processo; ao voltar, as amostras antigas
        // virariam um pico falso.
        _processos.Reiniciar();
    }

    private void AbrirDesempenho()
    {
        new DesempenhoWindow { Owner = this }.ShowDialog();

        // O plano pode ter mudado la dentro.
        SincronizarPerfil();
    }

    /// <summary>Tambem usada na primeira execucao, pela janela principal.</summary>
    public void AbrirConfiguracao(bool primeiraVez)
    {
        var tela = new ConfiguracaoWindow(_cfg, _sensores, primeiraVez) { Owner = this };
        tela.ShowDialog();

        // Os limites mudam a cor do anel, entao vale repintar sem esperar a
        // proxima leitura.
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    private void Pausar_Click(object sender, RoutedEventArgs e)
    {
        _pausado = !_pausado;
        BotaoPausar.Opacity = _pausado ? 0.45 : 1.0;
        BotaoPausar.ToolTip = _pausado ? "Retomar monitoramento" : "Pausar monitoramento";
    }
}
