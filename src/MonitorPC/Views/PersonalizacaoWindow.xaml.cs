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
    /// Aberta pela propria Kisland: a tela ja comeca na aba dela, e nao na do
    /// painel, que nao e o que a pessoa veio mudar.
    /// </param>
    public PersonalizacaoWindow(AppConfig cfg, Action aoMudar, bool irParaIlha = false)
    {
        InitializeComponent();

        if (irParaIlha) AbaDaKisland.IsChecked = true;
        Janelas.Acompanhar(this);
        _cfg = cfg;
        _aoMudar = aoMudar;

        MontarPaleta();
        MontarIas();

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

    /// <summary>
    /// Troca entre as abas Painel e Kisland. Dispara tambem durante o
    /// InitializeComponent, pela aba marcada no XAML, antes de as abas
    /// existirem — dai a conferencia.
    /// </summary>
    private void Aba_Alterada(object sender, RoutedEventArgs e)
    {
        if (AbaPainel is null || AbaKisland is null) return;

        var kisland = AbaDaKisland.IsChecked == true;
        AbaPainel.Visibility = kisland ? Visibility.Collapsed : Visibility.Visible;
        AbaKisland.Visibility = kisland ? Visibility.Visible : Visibility.Collapsed;
        Rolagem?.ScrollToTop();
    }

    private void OpacidadeIlha_Alterada(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ValorOpacidadeIlha is not null) ValorOpacidadeIlha.Text = $"{e.NewValue:F0}%";
        if (_carregando) return;

        _cfg.IlhaOpacidade = (int)e.NewValue;
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
        OpacidadeIlha.Value = Math.Clamp(_cfg.IlhaOpacidade ?? _cfg.Opacidade, AppConfig.OpacidadeMinima, 100);

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
        IlhaData.IsChecked = _cfg.IlhaData;
        IlhaAbertaCalendario.IsChecked = _cfg.IlhaAbertaCalendario;
        IlhaAbertaAtalhos.IsChecked = _cfg.IlhaAbertaAtalhos;
        IlhaSaidasDeSom.IsChecked = _cfg.IlhaSaidasDeSom;
        IlhaJogoCpu.IsChecked = _cfg.IlhaJogoCpu;
        IlhaJogoCpuUso.IsChecked = _cfg.IlhaJogoCpuUso;
        IlhaJogoGpu.IsChecked = _cfg.IlhaJogoGpu;
        IlhaJogoGpuUso.IsChecked = _cfg.IlhaJogoGpuUso;
        IlhaJogoRam.IsChecked = _cfg.IlhaJogoRam;
        IlhaJogoPotencia.IsChecked = _cfg.IlhaJogoPotencia;
        IlhaJogoRelogio.IsChecked = _cfg.IlhaJogoRelogio;
        IlhaJogoMusica.IsChecked = _cfg.IlhaJogoMusica;
        IlhaBandeja.IsChecked = _cfg.IlhaBandeja;
        IlhaRodaVolume.IsChecked = _cfg.IlhaRodaMudaVolume;
        IlhaIa.SelectedIndex = Math.Max(0, _idsDeIa.IndexOf(_cfg.IlhaIa ?? ""));
        IlhaIaEndereco.Text = _cfg.IlhaIaEndereco ?? "";
        MontarListaDeAtalhos();
        IlhaBotaoPersonalizacao.IsChecked = _cfg.IlhaBotaoPersonalizacao;
        IlhaAbertaPlayer.IsChecked = _cfg.IlhaAbertaPlayer;
        IlhaAbertaCpu.IsChecked = _cfg.AbertaCpu;
        IlhaAbertaGpu.IsChecked = _cfg.AbertaGpu;
        IlhaAbertaRam.IsChecked = _cfg.AbertaRam;
        IlhaAbertaDisco.IsChecked = _cfg.AbertaDisco;
        IlhaAbertaEnergia.IsChecked = _cfg.AbertaEnergia;
        IlhaAbertaVolume.IsChecked = _cfg.IlhaAbertaVolume;
        IlhaAbertaPerfis.IsChecked = _cfg.IlhaAbertaPerfis;
        IlhaFps.IsChecked = _cfg.IlhaFps;
        IlhaFicarEmTelaCheia.IsChecked = _cfg.IlhaFicarEmTelaCheia;

        // A contagem usa o rastreamento de eventos do Windows, que exige o app
        // elevado. Sem isso a caixa fica apagada e diz por quê, em vez de
        // ligar algo que nunca vai mostrar nada.
        if (!AtualizacaoService.Elevado())
        {
            IlhaFps.IsEnabled = false;
            IlhaFicarEmTelaCheia.IsEnabled = false;
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
        _cfg.IlhaData = IlhaData.IsChecked == true;
        _cfg.IlhaAbertaCalendario = IlhaAbertaCalendario.IsChecked == true;
        _cfg.IlhaAbertaAtalhos = IlhaAbertaAtalhos.IsChecked == true;
        _cfg.IlhaSaidasDeSom = IlhaSaidasDeSom.IsChecked == true;
        _cfg.IlhaJogoCpu = IlhaJogoCpu.IsChecked == true;
        _cfg.IlhaJogoCpuUso = IlhaJogoCpuUso.IsChecked == true;
        _cfg.IlhaJogoGpu = IlhaJogoGpu.IsChecked == true;
        _cfg.IlhaJogoGpuUso = IlhaJogoGpuUso.IsChecked == true;
        _cfg.IlhaJogoRam = IlhaJogoRam.IsChecked == true;
        _cfg.IlhaJogoPotencia = IlhaJogoPotencia.IsChecked == true;
        _cfg.IlhaJogoRelogio = IlhaJogoRelogio.IsChecked == true;
        _cfg.IlhaJogoMusica = IlhaJogoMusica.IsChecked == true;
        _cfg.IlhaBandeja = IlhaBandeja.IsChecked == true;
        _cfg.IlhaRodaMudaVolume = IlhaRodaVolume.IsChecked == true;
        _cfg.IlhaBotaoPersonalizacao = IlhaBotaoPersonalizacao.IsChecked == true;
        _cfg.IlhaAbertaPlayer = IlhaAbertaPlayer.IsChecked == true;
        _cfg.IlhaAbertaCpu = IlhaAbertaCpu.IsChecked == true;
        _cfg.IlhaAbertaGpu = IlhaAbertaGpu.IsChecked == true;
        _cfg.IlhaAbertaRam = IlhaAbertaRam.IsChecked == true;
        _cfg.IlhaAbertaDisco = IlhaAbertaDisco.IsChecked == true;
        _cfg.IlhaAbertaEnergia = IlhaAbertaEnergia.IsChecked == true;
        _cfg.IlhaAbertaVolume = IlhaAbertaVolume.IsChecked == true;
        _cfg.IlhaAbertaPerfis = IlhaAbertaPerfis.IsChecked == true;
        _cfg.IlhaFps = IlhaFps.IsChecked == true;
        _cfg.IlhaFicarEmTelaCheia = IlhaFicarEmTelaCheia.IsChecked == true;
        _cfg.IlhaAbrirAoPassarMouse = IlhaAoPassar.IsChecked == true;
        _cfg.IlhaEsconderEmTelaCheia = IlhaTelaCheia.IsChecked == true;
        _cfg.IlhaAvisoDeVolume = IlhaAvisoVolume.IsChecked == true;
        _cfg.IlhaAvisoDeFaixa = IlhaAvisoFaixa.IsChecked == true;

        AtualizarEstados();
        Confirmar();
    }

    // ------------------------------------------------------------------
    // Atalhos da Kisland

    /// <summary>Id de cada linha do seletor de chat, na mesma ordem dos itens.</summary>
    private readonly List<string> _idsDeIa = new();

    private void MontarIas()
    {
        IlhaIa.Items.Add("Nenhum");
        _idsDeIa.Add("");

        foreach (var ia in AtalhoService.Ias)
        {
            IlhaIa.Items.Add(ia.Nome);
            _idsDeIa.Add(ia.Id);
        }

        IlhaIa.Items.Add("Outro");
        _idsDeIa.Add(AtalhoService.IaOutra);
    }

    private void Ia_Escolhida(object sender, SelectionChangedEventArgs e)
    {
        var indice = IlhaIa.SelectedIndex;
        var id = indice >= 0 && indice < _idsDeIa.Count ? _idsDeIa[indice] : "";

        if (LinhaIaEndereco is not null)
            LinhaIaEndereco.Visibility = id == AtalhoService.IaOutra ? Visibility.Visible : Visibility.Collapsed;

        if (_carregando) return;

        _cfg.IlhaIa = id.Length == 0 ? null : id;
        Confirmar();
    }

    /// <summary>
    /// As dicas dentro das caixas de texto somem quando ha texto. O endereco
    /// do chat proprio vale a cada letra, como o resto da Personalizacao.
    /// </summary>
    private void Texto_Alterado(object sender, TextChangedEventArgs e)
    {
        if (DicaIaEndereco is not null) DicaIaEndereco.Visibility = Dica(IlhaIaEndereco);
        if (DicaEnderecoDoSite is not null) DicaEnderecoDoSite.Visibility = Dica(EnderecoDoSite);
        if (DicaNomeDoSite is not null) DicaNomeDoSite.Visibility = Dica(NomeDoSite);
        if (ErroDoSite is not null) ErroDoSite.Visibility = Visibility.Collapsed;

        if (_carregando || !ReferenceEquals(sender, IlhaIaEndereco)) return;

        _cfg.IlhaIaEndereco = IlhaIaEndereco.Text.Trim();
        Confirmar();
    }

    private static Visibility Dica(TextBox? caixa) =>
        caixa is null || caixa.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Uma linha por atalho: icone, nome, destino e o botao de tirar.</summary>
    private void MontarListaDeAtalhos()
    {
        ListaAtalhos.Children.Clear();

        for (var i = 0; i < _cfg.IlhaAtalhos.Count; i++)
        {
            var atalho = _cfg.IlhaAtalhos[i];

            var linha = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var circulo = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15) };
            circulo.SetResourceReference(Border.BackgroundProperty, "Chip");

            if (AtalhoService.Icone(atalho.Destino) is { } icone)
            {
                var imagem = new Image { Source = icone, Width = 18, Height = 18 };
                RenderOptions.SetBitmapScalingMode(imagem, BitmapScalingMode.HighQuality);
                circulo.Child = imagem;
            }
            else
            {
                var letra = new TextBlock
                {
                    Text = Inicial(atalho.Nome),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                letra.SetResourceReference(TextBlock.ForegroundProperty, "Acento");
                circulo.Child = letra;
            }

            var textos = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var nome = new TextBlock
            {
                Text = atalho.Nome,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
            var destino = new TextBlock
            {
                Text = Resumo(atalho.Destino),
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = atalho.Destino
            };
            destino.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            textos.Children.Add(nome);
            textos.Children.Add(destino);
            Grid.SetColumn(textos, 1);

            var x = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform
            };
            x.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Icone");
            var tirar = new Button
            {
                Style = (Style)FindResource("BotaoIcone"),
                Width = 28,
                Height = 28,
                Content = x,
                Tag = i,
                ToolTip = "Tirar da Kisland",
                VerticalAlignment = VerticalAlignment.Center
            };
            tirar.Click += TirarAtalho_Click;
            Grid.SetColumn(tirar, 2);

            linha.Children.Add(circulo);
            linha.Children.Add(textos);
            linha.Children.Add(tirar);
            ListaAtalhos.Children.Add(linha);
        }

        var cheio = _cfg.IlhaAtalhos.Count >= AtalhoService.Maximo;
        BotoesAtalho.IsEnabled = !cheio;
        BotoesAtalho.Opacity = cheio ? 0.5 : 1;
        DicaAtalhos.Text = cheio
            ? $"Chegou ao limite de {AtalhoService.Maximo}. Tire um para colocar outro."
            : $"Aparecem com a Kisland aberta, até {AtalhoService.Maximo}.";
    }

    private static string Inicial(string nome)
    {
        var letra = nome.FirstOrDefault(char.IsLetterOrDigit);
        return letra == default ? "?" : char.ToUpperInvariant(letra).ToString();
    }

    /// <summary>"youtube.com" para um site, "Spotify.exe" para um app.</summary>
    private static string Resumo(string destino) =>
        Uri.TryCreate(destino, UriKind.Absolute, out var uri) && !uri.IsFile
            ? AtalhoService.Host(uri)
            : System.IO.Path.GetFileName(destino);

    private void Adicionar(AtalhoDaIlha atalho)
    {
        if (_cfg.IlhaAtalhos.Count >= AtalhoService.Maximo) return;

        _cfg.IlhaAtalhos.Add(atalho);
        MontarListaDeAtalhos();
        Confirmar();
    }

    private void TirarAtalho_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int i } || i < 0 || i >= _cfg.IlhaAtalhos.Count) return;

        _cfg.IlhaAtalhos.RemoveAt(i);
        MontarListaDeAtalhos();
        Confirmar();
    }

    /// <summary>
    /// Escolher um app. Comeca no menu Iniciar, onde estao os atalhos de tudo
    /// o que foi instalado; escolhido um atalho, vale o app para onde ele
    /// aponta, com o icone e o nome dele.
    /// </summary>
    private void AdicionarApp_Click(object sender, RoutedEventArgs e)
    {
        var dialogo = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha um app para a Kisland",
            Filter = "Apps e atalhos (*.exe;*.lnk)|*.exe;*.lnk",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        if (dialogo.ShowDialog(this) != true) return;

        Adicionar(new AtalhoDaIlha
        {
            Nome = AtalhoService.NomeDoApp(dialogo.FileName),
            Destino = dialogo.FileName
        });
    }

    private void AdicionarSite_Click(object sender, RoutedEventArgs e)
    {
        NovoSite.Visibility = Visibility.Visible;
        BotoesAtalho.Visibility = Visibility.Collapsed;
        EnderecoDoSite.Focus();
    }

    private void ConfirmarSite_Click(object sender, RoutedEventArgs e)
    {
        if (AtalhoService.NormalizarSite(EnderecoDoSite.Text) is not { } endereco)
        {
            ErroDoSite.Visibility = Visibility.Visible;
            return;
        }

        var nome = NomeDoSite.Text.Trim();
        if (nome.Length == 0)
        {
            // "youtube.com" vira "Youtube": a primeira parte do endereco.
            var host = AtalhoService.Host(new Uri(endereco));
            var parte = host.Split('.')[0];
            nome = parte.Length == 0 ? host : char.ToUpperInvariant(parte[0]) + parte[1..];
        }

        FecharNovoSite();
        Adicionar(new AtalhoDaIlha { Nome = nome, Destino = endereco });
    }

    private void CancelarSite_Click(object sender, RoutedEventArgs e) => FecharNovoSite();

    private void FecharNovoSite()
    {
        EnderecoDoSite.Text = "";
        NomeDoSite.Text = "";
        ErroDoSite.Visibility = Visibility.Collapsed;
        NovoSite.Visibility = Visibility.Collapsed;
        BotoesAtalho.Visibility = Visibility.Visible;
    }

    private void Restaurar_Click(object sender, RoutedEventArgs e)
    {
        var padrao = new AppConfig();

        _cfg.AcompanharCorDoSistema = padrao.AcompanharCorDoSistema;
        _cfg.CorDestaque = padrao.CorDestaque;
        _cfg.Transparencia = padrao.Transparencia;
        _cfg.Opacidade = padrao.Opacidade;
        _cfg.IlhaOpacidade = padrao.IlhaOpacidade;
        _cfg.Tema = padrao.Tema;

        _carregando = true;
        TemaEscuro.IsChecked = _cfg.Tema == Tema.Escuro;
        TemaClaro.IsChecked = _cfg.Tema == Tema.Claro;
        TemaSistema.IsChecked = _cfg.Tema == Tema.Sistema;

        SeguirSistema.IsChecked = _cfg.AcompanharCorDoSistema;
        Transparencia.IsChecked = _cfg.Transparencia;
        Opacidade.Value = _cfg.Opacidade;
        OpacidadeIlha.Value = Math.Clamp(_cfg.IlhaOpacidade ?? _cfg.Opacidade, AppConfig.OpacidadeMinima, 100);
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
        OpacidadeIlha.IsEnabled = Transparencia.IsChecked == true;
        DicaOpacidadeIlha.Visibility = Transparencia.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

        // So faz sentido com o contador: e ele que diz qual janela e o jogo.
        // A excecao so existe com a regra (esconder) e o contador ligados.
        var excecaoValida = IlhaTelaCheia.IsChecked == true && IlhaFps.IsChecked == true && IlhaFps.IsEnabled;
        IlhaFicarEmTelaCheia.IsEnabled = excecaoValida;

        // A lista do modo jogo so vale com o contador ligado.
        var comFps = IlhaFps.IsChecked == true && IlhaFps.IsEnabled;
        CartaoModoJogo.IsEnabled = comFps;
        CartaoModoJogo.Opacity = comFps ? 1 : 0.5;
        DicaModoJogo.Text = comFps
            ? "Quando o jogo está em tela cheia no mesmo monitor que a Kisland. O FPS aparece sempre."
            : "Ligue \"FPS em jogos\" acima para a Kisland saber quando você está jogando.";
        LinhaFpsTelaCheia.Opacity = excecaoValida ? 1 : 0.5;

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
