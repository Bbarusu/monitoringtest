using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class PersonalizacaoWindow : Window
{
    /// <summary>Paleta oferecida quando o usuario nao acompanha a cor do sistema.</summary>
    private static readonly (string Nome, string Hex)[] Paleta =
    {
        ("Azul", "#3B82F6"),
        ("Violeta", "#8B5CF6"),
        ("Turquesa", "#14B8A6"),
        ("Verde", "#22C55E"),
        ("Laranja", "#F97316"),
        ("Rosa", "#EC4899")
    };

    private readonly AppConfig _cfg;
    private readonly Action _aoMudar;
    private bool _carregando = true;

    public PersonalizacaoWindow(AppConfig cfg, Action aoMudar)
    {
        InitializeComponent();
        _cfg = cfg;
        _aoMudar = aoMudar;

        MontarPaleta();

        SeguirSistema.IsChecked = _cfg.AcompanharCorDoSistema;
        Transparencia.IsChecked = _cfg.Transparencia;
        Opacidade.Value = Math.Clamp(_cfg.Opacidade, 40, 100);

        AtualizarEstados();
        AplicarFundo();

        _carregando = false;
    }

    private void MontarPaleta()
    {
        foreach (var (nome, hex) in Paleta)
        {
            var cor = (Color)ColorConverter.ConvertFromString(hex)!;
            var botao = new RadioButton
            {
                Style = (Style)FindResource("Amostra"),
                Background = new SolidColorBrush(cor),
                Tag = hex,
                ToolTip = nome,
                IsChecked = string.Equals(hex, _cfg.CorDestaque, StringComparison.OrdinalIgnoreCase)
            };
            System.Windows.Automation.AutomationProperties.SetName(botao, nome);
            botao.Checked += Cor_Escolhida;
            Cores.Children.Add(botao);
        }
    }

    // ------------------------------------------------------------------

    private void Cor_Escolhida(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;
        if (sender is not RadioButton { Tag: string hex }) return;

        _cfg.CorDestaque = hex;
        Confirmar();
    }

    private void SeguirSistema_Alterado(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;
        _cfg.AcompanharCorDoSistema = SeguirSistema.IsChecked == true;
        AtualizarEstados();
        Confirmar();
    }

    private void Transparencia_Alterada(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;
        _cfg.Transparencia = Transparencia.IsChecked == true;
        AtualizarEstados();
        Confirmar();
    }

    private void Opacidade_Alterada(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ValorOpacidade.Text = $"{e.NewValue:F0}%";
        if (_carregando) return;

        _cfg.Opacidade = (int)e.NewValue;
        Confirmar();
    }

    private void Restaurar_Click(object sender, RoutedEventArgs e)
    {
        var padrao = new AppConfig();

        _cfg.AcompanharCorDoSistema = padrao.AcompanharCorDoSistema;
        _cfg.CorDestaque = padrao.CorDestaque;
        _cfg.Transparencia = padrao.Transparencia;
        _cfg.Opacidade = padrao.Opacidade;

        _carregando = true;
        SeguirSistema.IsChecked = _cfg.AcompanharCorDoSistema;
        Transparencia.IsChecked = _cfg.Transparencia;
        Opacidade.Value = _cfg.Opacidade;
        foreach (var filho in Cores.Children.OfType<RadioButton>())
            filho.IsChecked = string.Equals((string)filho.Tag, _cfg.CorDestaque, StringComparison.OrdinalIgnoreCase);
        _carregando = false;

        AtualizarEstados();
        Confirmar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------

    /// <summary>Aplica na hora, para o efeito ser visto enquanto se mexe.</summary>
    private void Confirmar()
    {
        _cfg.Salvar();
        _aoMudar();
        AplicarFundo();
    }

    private void AtualizarEstados()
    {
        var seguindo = SeguirSistema.IsChecked == true;
        foreach (var filho in Cores.Children.OfType<RadioButton>())
            filho.IsEnabled = !seguindo;

        DicaCor.Text = seguindo
            ? "Desligue o acompanhamento para escolher uma cor."
            : "A cor vale para anéis, abas e botões do painel.";

        Opacidade.IsEnabled = Transparencia.IsChecked == true;
    }

    /// <summary>A propria tela usa a transparencia escolhida, como previa ao vivo.</summary>
    private void AplicarFundo()
    {
        var fundo = Color.FromRgb(0x15, 0x17, 0x1C);

        if (!_cfg.Transparencia)
        {
            Painel.Background = new SolidColorBrush(fundo);
            return;
        }

        var alpha = (byte)(Math.Clamp(_cfg.Opacidade, 40, 100) * 255 / 100);
        Painel.Background = new SolidColorBrush(
            Color.FromArgb(alpha, fundo.R, fundo.G, fundo.B));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateLayout();

        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }
}
