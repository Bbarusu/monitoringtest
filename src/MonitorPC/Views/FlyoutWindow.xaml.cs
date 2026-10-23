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
    private enum Aba { Geral, Cpu }

    private const double Raio = 100;
    private const double RaioCpu = 69;

    /// <summary>5 minutos de historico a 1 s, que e o que o grafico mostra.</summary>
    private const int PontosDoHistorico = 300;

    private static readonly Color FundoSolido = Color.FromRgb(0x15, 0x17, 0x1C);

    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly Queue<double> _historicoCpu = new();

    private Aba _aba = Aba.Geral;
    private bool _pausado;
    private bool _trocandoPerfil;
    private DateTime _recadoAte;
    private bool _posicionando;

    public FlyoutWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        _cfg = cfg;
        _sensores = sensores;

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

        _aba = ReferenceEquals(sender, AbaCpu) ? Aba.Cpu : Aba.Geral;

        var geral = _aba == Aba.Geral;
        TopoGeral.Visibility = geral ? Visibility.Visible : Visibility.Collapsed;
        BaseGeral.Visibility = geral ? Visibility.Visible : Visibility.Collapsed;
        TopoCpu.Visibility = geral ? Visibility.Collapsed : Visibility.Visible;
        BaseCpu.Visibility = geral ? Visibility.Collapsed : Visibility.Visible;

        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);

        // A altura muda com a aba, entao o painel precisa se reancorar no canto.
        if (IsVisible) Posicionar();
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

        GuardarHistorico(s);

        if (_aba == Aba.Geral) AplicarGeral(s);
        else AplicarCpu(s);

        AtualizarStatus(s);
    }

    private void GuardarHistorico(Snapshot s)
    {
        if (s.CpuUso is not { } uso) return;

        _historicoCpu.Enqueue(Math.Clamp(uso, 0, 100));
        while (_historicoCpu.Count > PontosDoHistorico) _historicoCpu.Dequeue();
    }

    private void AplicarGeral(Snapshot s)
    {
        Titulo.Text = "Meu PC";
        ChipEsquerdo.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C";
        ChipDireito.Text = s.RamUso is { } r ? $"RAM {r:F0}%" : "RAM --";

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
        ChipEsquerdo.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C";
        ChipDireito.Text = s.CpuClockMhz is > 0 ? Clock(s.CpuClockMhz!.Value) : "-- GHz";

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

        DesenharHistorico(s.CpuPicoDeNucleo);
        MontarGrade(s);
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
            linha.Atualizar($"{prefixo}{i + 1}", valores[i], clock);
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

    private void AreaGrafico_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DesenharHistorico(_sensores.Ultimo?.CpuPicoDeNucleo);

    private void DesenharHistorico(float? picoDeNucleo)
    {
        var largura = AreaGrafico.ActualWidth;
        var altura = AreaGrafico.ActualHeight;

        if (largura <= 0 || altura <= 0 || _historicoCpu.Count < 2)
        {
            GraficoLinha.Points = new PointCollection();
            GraficoArea.Points = new PointCollection();
            CpuPicoHistorico.Text = TextoDoPico(picoDeNucleo);
            return;
        }

        var valores = _historicoCpu.ToArray();

        // O grafico ocupa sempre a largura toda: com poucos pontos ele cresce da
        // esquerda para a direita conforme o historico enche.
        var passo = largura / Math.Max(valores.Length - 1, 1);

        var linha = new PointCollection(valores.Length);
        for (var i = 0; i < valores.Length; i++)
        {
            var y = altura - valores[i] / 100.0 * (altura - 4) - 2;
            linha.Add(new Point(i * passo, y));
        }

        GraficoLinha.Points = linha;

        // A area e a mesma linha fechada na base.
        var area = new PointCollection(linha) { new(largura, altura), new(0, altura) };
        GraficoArea.Points = area;

        CpuPicoHistorico.Text = TextoDoPico(picoDeNucleo);
    }

    /// <summary>Carga da thread mais ocupada agora, nao o pico do historico.</summary>
    private static string TextoDoPico(float? picoDeNucleo) =>
        picoDeNucleo is { } p ? $"pico de núcleo {p:F0}%" : "";

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

    private void Pausar_Click(object sender, RoutedEventArgs e)
    {
        _pausado = !_pausado;
        BotaoPausar.Opacity = _pausado ? 0.45 : 1.0;
        BotaoPausar.ToolTip = _pausado ? "Retomar monitoramento" : "Pausar monitoramento";
    }
}
