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

    /// <param name="irParaIlha">
    /// Aberta pela propria ilha: a tela ja comeca rolada ate a secao dela, em
    /// vez de no tema, que nao e o que a pessoa veio mudar.
    /// </param>
    public PersonalizacaoWindow(AppConfig cfg, Action aoMudar, bool irParaIlha = false)
    {
        InitializeComponent();

        if (irParaIlha)
        {
            // Depois do layout: antes dele a secao ainda nao tem posicao.
            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var y = SecaoIlha.TransformToAncestor(Conteudo).Transform(new Point(0, 0)).Y;
                    Rolagem.ScrollToVerticalOffset(y);
                }
                catch (InvalidOperationException)
                {
                    // Sem arvore visual montada: fica no topo, que tambem serve.
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Janelas.Acompanhar(this);
        _cfg = cfg;
        _aoMudar = aoMudar;

        MontarPaleta();

        Carregar();

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

    private void Tema_Alterado(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;

        _cfg.Tema = TemaClaro.IsChecked == true ? Tema.Claro
                  : TemaSistema.IsChecked == true ? Tema.Sistema
                  : Tema.Escuro;

        Confirmar();
    }

    private void SeguirSistema_Alterado(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;
        _cfg.AcompanharCorDoSistema = SeguirSistema.IsChecked == true;

        // Acompanhar o sistema vale para o conjunto: cor de destaque e tema.
        if (_cfg.AcompanharCorDoSistema)
        {
            _cfg.Tema = Tema.Sistema;
            _carregando = true;
            TemaSistema.IsChecked = true;
            _carregando = false;
        }

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

    /// <summary>
    /// Poe na tela o que esta na configuracao. Tambem roda na volta do guia
    /// da Kisland, que muda a configuracao por conta propria.
    /// </summary>
    private void Carregar()
    {
        TemaEscuro.IsChecked = _cfg.Tema == Tema.Escuro;
        TemaClaro.IsChecked = _cfg.Tema == Tema.Claro;
        TemaSistema.IsChecked = _cfg.Tema == Tema.Sistema;

        SeguirSistema.IsChecked = _cfg.AcompanharCorDoSistema;
        Transparencia.IsChecked = _cfg.Transparencia;
        Opacidade.Value = Math.Clamp(_cfg.Opacidade, AppConfig.OpacidadeMinima, 100);

        IlhaAtiva.IsChecked = _cfg.IlhaAtiva;
        IlhaMusica.IsChecked = _cfg.IlhaMusica;
        IlhaCpu.IsChecked = _cfg.IlhaCpu;
        IlhaGpu.IsChecked = _cfg.IlhaGpu;
        IlhaVolume.IsChecked = _cfg.IlhaVolume;
        IlhaRam.IsChecked = _cfg.IlhaRam;
        IlhaCpuUso.IsChecked = _cfg.IlhaCpuUso;
        IlhaGpuUso.IsChecked = _cfg.IlhaGpuUso;
        IlhaDisco.IsChecked = _cfg.IlhaDisco;
        IlhaPotencia.IsChecked = _cfg.IlhaPotencia;
        IlhaRelogio.IsChecked = _cfg.IlhaRelogio;
        IlhaBotaoPersonalizacao.IsChecked = _cfg.IlhaBotaoPersonalizacao;
        IlhaVidro.IsChecked = _cfg.IlhaVidro;
        IlhaFps.IsChecked = _cfg.IlhaFps;

        // A contagem usa o rastreamento de eventos do Windows, que exige o app
        // elevado. Sem isso a caixa fica apagada e diz por quê, em vez de
        // ligar algo que nunca vai mostrar nada.
        if (!AtualizacaoService.Elevado())
        {
            IlhaFps.IsEnabled = false;
            DetalheFps.Text = "Precisa do KInfo instalado com acesso completo aos sensores (rodando como administrador).";
        }
        EscalaIlha.Value = Math.Clamp(_cfg.IlhaEscala, 70, 100);
        IlhaAoPassar.IsChecked = _cfg.IlhaAbrirAoPassarMouse;
        IlhaAoClicar.IsChecked = !_cfg.IlhaAbrirAoPassarMouse;
        IlhaTelaCheia.IsChecked = _cfg.IlhaEsconderEmTelaCheia;
        IlhaAvisoVolume.IsChecked = _cfg.IlhaAvisoDeVolume;
        IlhaAvisoFaixa.IsChecked = _cfg.IlhaAvisoDeFaixa;

        foreach (var filho in Cores.Children.OfType<RadioButton>())
            filho.IsChecked = string.Equals((string)filho.Tag, _cfg.CorDestaque, StringComparison.OrdinalIgnoreCase);
    }

    private void EscalaIlha_Alterada(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ValorEscalaIlha.Text = $"{e.NewValue:F0}%";
        if (_carregando) return;

        _cfg.IlhaEscala = (int)e.NewValue;
        Confirmar();
    }

    private void GuiaKisland_Click(object sender, RoutedEventArgs e)
    {
        new GuiaKislandWindow(_cfg, _aoMudar) { Owner = this }.ShowDialog();

        _carregando = true;
        Carregar();
        _carregando = false;

        AtualizarEstados();
        AplicarFundo();
    }

    /// <summary>
    /// Todas as opcoes da ilha passam por aqui: sao muitas e independentes, e
    /// ler o conjunto inteiro a cada mudanca e mais simples que um handler por
    /// caixa — e nao ha como uma ficar fora de sincronia com a tela.
    /// </summary>
    private void Ilha_Alterada(object sender, RoutedEventArgs e)
    {
        if (_carregando) return;

        _cfg.IlhaAtiva = IlhaAtiva.IsChecked == true;
        _cfg.IlhaMusica = IlhaMusica.IsChecked == true;
        _cfg.IlhaCpu = IlhaCpu.IsChecked == true;
        _cfg.IlhaGpu = IlhaGpu.IsChecked == true;
        _cfg.IlhaVolume = IlhaVolume.IsChecked == true;
        _cfg.IlhaRam = IlhaRam.IsChecked == true;
        _cfg.IlhaCpuUso = IlhaCpuUso.IsChecked == true;
        _cfg.IlhaGpuUso = IlhaGpuUso.IsChecked == true;
        _cfg.IlhaDisco = IlhaDisco.IsChecked == true;
        _cfg.IlhaPotencia = IlhaPotencia.IsChecked == true;
        _cfg.IlhaRelogio = IlhaRelogio.IsChecked == true;
        _cfg.IlhaBotaoPersonalizacao = IlhaBotaoPersonalizacao.IsChecked == true;
        _cfg.IlhaVidro = IlhaVidro.IsChecked == true;
        _cfg.IlhaFps = IlhaFps.IsChecked == true;
        _cfg.IlhaAbrirAoPassarMouse = IlhaAoPassar.IsChecked == true;
        _cfg.IlhaEsconderEmTelaCheia = IlhaTelaCheia.IsChecked == true;
        _cfg.IlhaAvisoDeVolume = IlhaAvisoVolume.IsChecked == true;
        _cfg.IlhaAvisoDeFaixa = IlhaAvisoFaixa.IsChecked == true;

        AtualizarEstados();
        Confirmar();
    }

    private void Restaurar_Click(object sender, RoutedEventArgs e)
    {
        var padrao = new AppConfig();

        _cfg.AcompanharCorDoSistema = padrao.AcompanharCorDoSistema;
        _cfg.CorDestaque = padrao.CorDestaque;
        _cfg.Transparencia = padrao.Transparencia;
        _cfg.Opacidade = padrao.Opacidade;
        _cfg.Tema = padrao.Tema;

        _carregando = true;
        TemaEscuro.IsChecked = _cfg.Tema == Tema.Escuro;
        TemaClaro.IsChecked = _cfg.Tema == Tema.Claro;
        TemaSistema.IsChecked = _cfg.Tema == Tema.Sistema;

        SeguirSistema.IsChecked = _cfg.AcompanharCorDoSistema;
        Transparencia.IsChecked = _cfg.Transparencia;
        Opacidade.Value = _cfg.Opacidade;
        TemaEscuro.IsChecked = _cfg.Tema == Tema.Escuro;
        TemaClaro.IsChecked = _cfg.Tema == Tema.Claro;
        TemaSistema.IsChecked = _cfg.Tema == Tema.Sistema;
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

        // Com a ilha desligada as opcoes dela ficam a mostra, mas apagadas:
        // assim da para ver o que ela faz antes de ligar.
        var ilha = IlhaAtiva.IsChecked == true;
        OpcoesIlha.IsEnabled = ilha;
        OpcoesIlha.Opacity = ilha ? 1 : 0.5;
    }

    /// <summary>A propria tela usa a transparencia escolhida, como previa ao vivo.</summary>
    private void AplicarFundo()
    {
        var fundo = ThemeService.CorDeFundo;

        if (!_cfg.Transparencia)
        {
            Painel.Background = new SolidColorBrush(fundo);
            return;
        }

        var alpha = (byte)(Math.Clamp(_cfg.Opacidade, AppConfig.OpacidadeMinima, 100) * 255 / 100);
        Painel.Background = new SolidColorBrush(
            Color.FromArgb(alpha, fundo.R, fundo.G, fundo.B));
    }

}
