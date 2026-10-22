using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class FlyoutWindow : Window
{
    private const double Raio = 100;
    private const double Margem = 20;

    private static readonly Color FundoSolido = Color.FromRgb(0x15, 0x17, 0x1C);

    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private bool _pausado;

    public FlyoutWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        _cfg = cfg;
        _sensores = sensores;

        AplicarTransparencia();
        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() => Aplicar(snap));

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
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    public void Mostrar()
    {
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);

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

        var alpha = (byte)(Math.Clamp(_cfg.Opacidade, 40, 100) * 255 / 100);
        Painel.Background = new SolidColorBrush(
            Color.FromArgb(alpha, FundoSolido.R, FundoSolido.G, FundoSolido.B));
    }

    // ------------------------------------------------------------------
    // Dados

    private void Aplicar(Snapshot s)
    {
        AvisoDriver.Visibility = s.SensoresDeBaixoNivel ? Visibility.Collapsed : Visibility.Visible;

        // Valor principal: temperatura da CPU; o anel mostra a carga.
        ValorPrincipal.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0}" : "--";
        LegendaPrincipal.Text = s.CpuUso is { } uso
            ? $"CPU · {uso:F0}% de carga"
            : "CPU";

        var carga = Math.Clamp(s.CpuUso ?? 0, 0, 100);
        DesenharAnel(carga);
        BarraCarga.Width = (ActualWidth - 2 * Margem - 42) * carga / 100.0;

        // Passar do limite configurado pinta o anel e o numero de laranja.
        var quente = s.CpuTemp is { } t && t >= _cfg.LimiteCpu;
        var cor = quente
            ? (Brush)Application.Current.Resources["Alerta"]
            : (Brush)Application.Current.Resources["Acento"];
        Anel.Stroke = cor;
        BarraCarga.Background = cor;
        ValorPrincipal.Foreground = quente
            ? (Brush)Application.Current.Resources["AlertaTexto"]
            : Brushes.White;

        ChipCpu.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C";
        ChipRam.Text = s.RamUso is { } r ? $"RAM {r:F0}%" : "RAM --";

        CardGpu.Text = s.GpuTemp is { } gt && s.GpuUso is { } gu
            ? $"{gt:F0}° · {gu:F0}%"
            : "--";

        CardRam.Text = s.RamUsadaGb is { } usada && s.RamTotalGb is { } total
            ? $"{usada:F1} / {total:F0} GB"
            : s.RamUso is { } ru ? $"{ru:F0}% em uso" : "--";

        var intervalo = _pausado ? "pausado" : "atualiza a cada 1 s";
        Status.Text = s.SensoresDeBaixoNivel
            ? $"Sensores ativos · {intervalo}"
            : $"Sensores parciais · {intervalo}";
    }

    /// <summary>Arco do topo no sentido horario, proporcional a carga.</summary>
    private void DesenharAnel(double porcentagem)
    {
        // Abaixo de uns 2% o arco vira um ponto solto no topo. Um minimo visual
        // mantem a leitura de "tem pouca carga" sem parecer defeito.
        var valor = Math.Clamp(porcentagem, 0, 100);
        if (valor > 0) valor = Math.Max(valor, 1.8);

        if (valor <= 0)
        {
            Anel.Data = null;
            return;
        }

        var angulo = Math.Min(valor, 99.999) / 100.0 * 2 * Math.PI;
        var centro = new Point(110, 110);
        var inicio = new Point(centro.X, centro.Y - Raio);
        var fim = new Point(
            centro.X + Raio * Math.Sin(angulo),
            centro.Y - Raio * Math.Cos(angulo));

        var arco = new ArcSegment(fim, new Size(Raio, Raio), 0,
            angulo > Math.PI, SweepDirection.Clockwise, true);

        var figura = new PathFigure(inicio, new[] { arco }, false);
        Anel.Data = new PathGeometry(new[] { figura });
    }

    private void Minimizar_Click(object sender, RoutedEventArgs e) => Esconder();

    private void Pausar_Click(object sender, RoutedEventArgs e)
    {
        _pausado = !_pausado;
        BotaoPausar.Opacity = _pausado ? 0.45 : 1.0;
        BotaoPausar.ToolTip = _pausado ? "Retomar monitoramento" : "Pausar monitoramento";
    }
}
