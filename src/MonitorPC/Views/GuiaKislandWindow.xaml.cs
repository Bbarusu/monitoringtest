using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// O guia da Kisland. Aparece sozinho uma vez, pelo convite do painel, e fica
/// na Personalizacao para quem quiser rever.
///
/// Os controles sao de verdade: ligar, escolher o que aparece, como abrir e a
/// cor gravam na configuracao na hora, e a Kisland muda junto. Um guia que so
/// mostrasse deixaria a pessoa repetir tudo depois na Personalizacao.
/// </summary>
public partial class GuiaKislandWindow : Window
{
    private const int Passos = 6;

    /// <summary>A mesma paleta da Personalizacao.</summary>
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
    private int _passo;

    public GuiaKislandWindow(AppConfig cfg, Action aoMudar)
    {
        InitializeComponent();
        _cfg = cfg;
        _aoMudar = aoMudar;

        Ligar.IsChecked = _cfg.IlhaAtiva;
        ItemFps.IsChecked = _cfg.IlhaFps;

        // Sem o app elevado o contador nao roda: a chave fica apagada e diz
        // por que, como na Personalizacao.
        if (!AtualizacaoService.Elevado())
        {
            ItemFps.IsEnabled = false;
            DetalheFps.Text = "Precisa do KInfo instalado com acesso completo aos sensores.";
        }
        AbrirPassando.IsChecked = _cfg.IlhaAbrirAoPassarMouse;
        AbrirClicando.IsChecked = !_cfg.IlhaAbrirAoPassarMouse;
        ItemMusica.IsChecked = _cfg.IlhaMusica;
        ItemCpu.IsChecked = _cfg.IlhaCpu;
        ItemGpu.IsChecked = _cfg.IlhaGpu;
        ItemVolume.IsChecked = _cfg.IlhaVolume;
        ItemRam.IsChecked = _cfg.IlhaRam;
        ItemRelogio.IsChecked = _cfg.IlhaRelogio;

        var hora = DateTime.Now.ToString("HH:mm");
        PreviaHora.Text = hora;
        PreviaHora5.Text = hora;

        MontarPaleta();
        MontarPontos();
        AtualizarPrevia();
        MostrarPasso(0);

        _carregando = false;
    }

    private void MontarPaleta()
    {
        foreach (var (nome, hex) in Paleta)
        {
            var botao = new RadioButton
            {
                Style = (Style)FindResource("Amostra"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!),
                Tag = hex,
                ToolTip = nome,
                GroupName = "CorDoGuia",

                // Acompanhando a cor do Windows, nenhuma aparece marcada: a
                // cor em uso nao e nenhuma das seis.
                IsChecked = !_cfg.AcompanharCorDoSistema &&
                            string.Equals(hex, _cfg.CorDestaque, StringComparison.OrdinalIgnoreCase)
            };
            System.Windows.Automation.AutomationProperties.SetName(botao, nome);
            botao.Checked += Cor_Escolhida;
            Cores.Children.Add(botao);
        }
    }

    private void MontarPontos()
    {
        for (var i = 0; i < Passos; i++)
            Pontos.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) });
    }

    // ------------------------------------------------------------------
    // Passos

    private void MostrarPasso(int passo)
    {
        _passo = Math.Clamp(passo, 0, Passos - 1);

        Passo1.Visibility = Visivel(_passo == 0);
        Passo2.Visibility = Visivel(_passo == 1);
        Passo3.Visibility = Visivel(_passo == 2);
        Passo4.Visibility = Visivel(_passo == 3);
        PassoFps.Visibility = Visivel(_passo == 4);
        Passo5.Visibility = Visivel(_passo == 5);

        Contagem.Text = $"Conhecendo a Kisland · {_passo + 1} de {Passos}";
        BotaoVoltar.Visibility = _passo == 0 ? Visibility.Hidden : Visibility.Visible;
        BotaoAvancar.Content = _passo == Passos - 1 ? "Pronto!" : "Próximo";

        for (var i = 0; i < Pontos.Children.Count; i++)
        {
            if (Pontos.Children[i] is not Border ponto) continue;

            ponto.Width = i == _passo ? 18 : 6;
            ponto.SetResourceReference(Border.BackgroundProperty, i == _passo ? "Acento" : "Trilho");
        }
    }

    private static Visibility Visivel(bool sim) => sim ? Visibility.Visible : Visibility.Collapsed;

    private void Voltar_Click(object sender, RoutedEventArgs e) => MostrarPasso(_passo - 1);

    private void Avancar_Click(object sender, RoutedEventArgs e)
    {
        if (_passo == Passos - 1)
        {
            Close();
            return;
        }

        MostrarPasso(_passo + 1);
    }

    private void Pular_Click(object sender, RoutedEventArgs e) => Close();

    private void Painel_Arrastar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject origem && DentroDeControle(origem)) return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private static bool DentroDeControle(DependencyObject? origem)
    {
        while (origem is not null)
        {
            if (origem is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (origem is not Visual) return false;
            origem = VisualTreeHelper.GetParent(origem);
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Opcoes, que valem na hora

    private void Opcao_Alterada(object sender, RoutedEventArgs e)
    {
        AtualizarPrevia();
        if (_carregando) return;

        _cfg.IlhaAtiva = Ligar.IsChecked == true;
        _cfg.IlhaAbrirAoPassarMouse = AbrirPassando.IsChecked == true;
        _cfg.IlhaMusica = ItemMusica.IsChecked == true;
        _cfg.IlhaCpu = ItemCpu.IsChecked == true;
        _cfg.IlhaGpu = ItemGpu.IsChecked == true;
        _cfg.IlhaVolume = ItemVolume.IsChecked == true;
        _cfg.IlhaRam = ItemRam.IsChecked == true;
        _cfg.IlhaRelogio = ItemRelogio.IsChecked == true;
        _cfg.IlhaFps = ItemFps.IsChecked == true;

        Confirmar();
    }

    /// <summary>
    /// Escolher uma das seis e deixar de acompanhar a cor do Windows: e o que
    /// a Personalizacao faz, e o guia faz igual.
    /// </summary>
    private void Cor_Escolhida(object sender, RoutedEventArgs e)
    {
        if (_carregando || sender is not RadioButton { Tag: string hex }) return;

        _cfg.AcompanharCorDoSistema = false;
        _cfg.CorDestaque = hex;
        Confirmar();
    }

    private void Confirmar()
    {
        _cfg.Salvar();
        _aoMudar();
    }

    /// <summary>A pilula do passo 3 mostra o que esta marcado, e o anel quando nada esta.</summary>
    private void AtualizarPrevia()
    {
        if (PreviaMusica is null) return;

        PreviaMusica.Visibility = Visivel(ItemMusica.IsChecked == true);
        PreviaCpu.Visibility = Visivel(ItemCpu.IsChecked == true);
        PreviaGpu.Visibility = Visivel(ItemGpu.IsChecked == true);
        PreviaRam.Visibility = Visivel(ItemRam.IsChecked == true);
        PreviaVolume.Visibility = Visivel(ItemVolume.IsChecked == true);
        PreviaRelogio.Visibility = Visivel(ItemRelogio.IsChecked == true);

        var algum = ItemMusica.IsChecked == true || ItemCpu.IsChecked == true ||
                    ItemGpu.IsChecked == true || ItemRam.IsChecked == true ||
                    ItemVolume.IsChecked == true || ItemRelogio.IsChecked == true;

        // Como na Kisland: com algo a mostrar, o botao de Personalizacao no
        // fim; sem nada, so o anel.
        PreviaAnel.Visibility = Visivel(!algum);
        PreviaBotao.Visibility = Visivel(algum && _cfg.IlhaBotaoPersonalizacao);
    }
}
