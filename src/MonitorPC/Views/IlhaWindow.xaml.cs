using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MonitorPC.Models;
using MonitorPC.Services;
using WF = System.Windows.Forms;

namespace MonitorPC.Views;

/// <summary>
/// A ilha do topo da tela: uma pilula com o que esta tocando, as temperaturas e
/// o volume, que abre no painel de bolso ao passar o mouse.
///
/// Nao le sensor nenhum por conta propria. As temperaturas chegam pela mesma
/// leitura que alimenta o icone da bandeja; musica e volume chegam por aviso do
/// Windows.
/// </summary>
public partial class IlhaWindow : Window
{
    /// <summary>
    /// Tempo com o cursor em cima antes de abrir, escolhido na Personalizacao.
    /// Passar de raspao nao abre; e o tempo da para clicar no anel ou num chip
    /// antes de a Kisland se abrir por cima deles.
    /// </summary>
    private TimeSpan EsperaParaAbrir => TimeSpan.FromMilliseconds(Math.Clamp(_cfg.IlhaEsperaParaAbrirMs, 150, 10000));

    /// <summary>Tempo com o cursor fora antes de fechar: escapar um pixel nao fecha.</summary>
    private static readonly TimeSpan EsperaParaFechar = TimeSpan.FromMilliseconds(450);

    /// <summary>Pausado ha tanto tempo, o player some da ilha.</summary>
    private static readonly TimeSpan SilencioQueLimpa = TimeSpan.FromMinutes(3);

    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly AlertService _alertas;
    private readonly MidiaService _midia;
    private readonly VolumeService _volume;

    private readonly DispatcherTimer _relogio;
    private readonly DispatcherTimer _cursor;
    private readonly DispatcherTimer _fimDoAviso;

    private enum Aviso { Nenhum, Volume, Faixa }

    private Aviso _aviso = Aviso.Nenhum;
    private bool _expandida;
    private bool _ativa;
    private bool _escondidaPorTelaCheia;
    private bool _ajustandoVolume;
    private bool _trocandoPerfil;
    private bool _equalizadorAndando;
    private bool _anelAndando;
    private bool _arrastando;
    private bool _exigirSaida;
    private int _segundos;
    private string? _ultimaFaixa;
    private IntPtr _handle;

    // Contador de FPS: a sessao de rastreamento, o jogo da vez, e se a
    // Kisland esta no modo enxuto de quando o jogo divide a tela com ela.
    private readonly FpsService _fps = new();
    private Jogo? _jogo;
    private bool _modoJogo;
    private int _tentarFpsEm;

    /// <summary>
    /// FPS mostrado, levemente suavizado: a media do ultimo segundo ja e
    /// estavel, mas oscilar um ou dois quadros de um segundo para o outro faz
    /// o numero tremer a toa. Mudanca grande (cena pesada, menu) passa direto.
    /// </summary>
    private double _fpsMostrado;

    private DateTime _diaDoCalendario;

    private DateTime? _dentroDesde;
    private DateTime? _foraDesde;

    // Limpeza do player depois de um tempo sem som.
    private DateTime _somDaMidiaEm = DateTime.UtcNow;
    private string? _faixaDispensada;
    private bool _tocandoAoDispensar;

    /// <summary>
    /// Ligada na Personalizacao. Diferente de IsVisible: escondida por tela
    /// cheia ela continua ativa, e volta sozinha quando a tela cheia acaba.
    /// </summary>
    public bool Ativa => _ativa;

    /// <summary>Pedidos que so o painel sabe atender.</summary>
    public event Action? PedirPainel;
    public event Action? PedirPersonalizacao;
    public event Action? PedirDesempenho;

    public IlhaWindow(AppConfig cfg, SensorService sensores, AlertService alertas,
                      MidiaService midia, VolumeService volume)
    {
        InitializeComponent();

        _cfg = cfg;
        _sensores = sensores;
        _alertas = alertas;
        _midia = midia;
        _volume = volume;

        _relogio = Temporizador(TimeSpan.FromSeconds(1), Relogio_Tick);
        _cursor = Temporizador(TimeSpan.FromMilliseconds(100), Cursor_Tick);
        _fimDoAviso = Temporizador(TimeSpan.FromSeconds(2), () => { _fimDoAviso.Stop(); EncerrarAviso(); });

        SourceInitialized += (_, _) =>
        {
            NaoRoubarFoco();

            // Roda do mouse, clique do meio e arquivos soltos chegam como
            // mensagens do Windows, lidas antes do WPF: a Kisland nunca e a
            // janela ativa, e o WPF nem sempre entrega a roda a quem nao tem
            // foco.
            HwndSource.FromHwnd(_handle)?.AddHook(Mensagem);
            PrepararBandeja();
        };
        SizeChanged += (_, _) => Posicionar();
        DpiChanged += (_, _) => Posicionar();

        // Monitor ligado, desligado ou com resolucao nova: a ilha volta ao
        // topo do monitor dela, ou do principal se o dela sumiu.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += Telas_Mudaram;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= Telas_Mudaram;

            // Os servicos vivem mais que a janela: sem soltar, uma janela
            // fechada continuaria recebendo os avisos.
            _sensores.Atualizado -= Sensores_Atualizados;
            _midia.Mudou -= Midia_Mudou;
            _volume.Mudou -= Volume_Mudou;
            _fps.Dispose();
            _kdrop?.Dispose();
        };

        _sensores.Atualizado += Sensores_Atualizados;
        _midia.Mudou += Midia_Mudou;
        _volume.Mudou += Volume_Mudou;

        AplicarConfiguracao();
    }

    private void Sensores_Atualizados(Snapshot snap) => Dispatcher.BeginInvoke(() => AplicarSensores(snap));
    private void Midia_Mudou(EstadoDeMidia? _) => Dispatcher.BeginInvoke(() => AplicarMidia(true));
    private void Volume_Mudou(int nivel, bool mudo) => Dispatcher.BeginInvoke(() => AplicarVolume(nivel, mudo, true));

    private static DispatcherTimer Temporizador(TimeSpan intervalo, Action acao)
    {
        var t = new DispatcherTimer { Interval = intervalo };
        t.Tick += (_, _) => acao();
        return t;
    }

    private void Telas_Mudaram(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Posicionar());

    // ------------------------------------------------------------------
    // Ligar, desligar e reagir a Personalizacao

    public void Ativar()
    {
        _ativa = true;
        AplicarConfiguracao();

        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AplicarMidia(false);
        AplicarVolume(_volume.Nivel, _volume.Mudo, false);
        AtualizarRelogio();

        _escondidaPorTelaCheia = false;
        Show();
        Posicionar();
        _relogio.Start();
        _cursor.Start();
        MostrarEstado();
    }

    public void Desativar()
    {
        _ativa = false;
        _relogio.Stop();
        _cursor.Stop();
        _fimDoAviso.Stop();
        _expandida = false;
        _aviso = Aviso.Nenhum;
        PararEqualizador();
        PararAnel();
        _fps.Parar();
        _jogo = null;
        _modoJogo = false;
        _escondidaPorInatividade = false;
        PararKdrop();
        Hide();
    }

    /// <summary>
    /// Aplica o que a Personalizacao decide: fundo, transparencia e o que a
    /// pilula mostra. Chamada a cada mudanca la, para o efeito ser visto na hora.
    /// </summary>
    public void AplicarConfiguracao()
    {
        AplicarPreta();

        // Mesma regra do painel: a transparencia vai no alpha do fundo, e nao na
        // opacidade da janela, que desbotaria texto e icones junto.
        var fundo = _cfg.IlhaPreta ? Colors.Black : ThemeService.CorDeFundo;
        if (TransparenciaDaIlha)
        {
            // A Kisland tem opacidade propria; sem ela definida, segue a do painel.
            var opacidade = _cfg.IlhaOpacidade ?? _cfg.Opacidade;
            var alpha = (byte)(Math.Clamp(opacidade, AppConfig.OpacidadeMinima, 100) * 255 / 100);
            Casca.Background = new SolidColorBrush(Color.FromArgb(alpha, fundo.R, fundo.G, fundo.B));
        }
        else
        {
            Casca.Background = new SolidColorBrush(fundo);
        }

        // Tamanho: a Kisland inteira em escala, texto e cantos juntos, para
        // continuar com as mesmas proporcoes em qualquer tamanho.
        var escala = Math.Clamp(_cfg.IlhaEscala, 70, 100) / 100.0;
        Casca.LayoutTransform = new ScaleTransform(escala, escala);

        AplicarContorno();

        // No modo jogo, o FPS, um aviso de temperatura se houver, e o que a
        // pessoa escolheu para o jogo na aba Kisland. CPU, GPU, RAM, potencia, relogio e musica seguem a lista do modo
        // jogo quando ele esta ativo; o resto so aparece fora dele.
        var normal = !_modoJogo;
        if (_cfg.IlhaFps && !_fps.Rodando) _tentarFpsEm = 0;
        ChipFps.Visibility = Visivel(_cfg.IlhaFps && _jogo is not null);
        ChipCpu.Visibility = Visivel(MostraCpuTemp || MostraCpuUso);
        ChipGpu.Visibility = Visivel(MostraGpuTemp || MostraGpuUso);
        ChipRam.Visibility = Visivel(MostraRam);
        ChipDisco.Visibility = Visivel(normal && _cfg.IlhaDisco);
        ChipPotencia.Visibility = Visivel(MostraPotencia);
        ChipVolume.Visibility = Visivel(normal && _cfg.IlhaVolume && _volume.Disponivel);
        var data = normal && _cfg.IlhaData;
        ChipRelogio.Visibility = Visivel(MostraRelogio || data);
        TextoRelogio.Visibility = Visivel(MostraRelogio);
        TextoData.Visibility = Visivel(data);
        IconeRelogio.Data = Geometry.Parse(MostraRelogio ? DesenhoRelogio : DesenhoCalendario);
        // O que aparece com ela aberta, como escolhido na aba Kisland.
        BlocoVolume.Visibility = Visivel(_volume.Disponivel && _cfg.IlhaAbertaVolume);
        CartaoCpu.Visibility = Visivel(_cfg.AbertaCpu);
        CartaoGpu.Visibility = Visivel(_cfg.AbertaGpu);
        CartaoRam.Visibility = Visivel(_cfg.AbertaRam);
        CartaoDisco.Visibility = Visivel(_cfg.AbertaDisco);
        CartaoEnergia.Visibility = Visivel(_cfg.AbertaEnergia);
        OrganizarCartoes();
        GradePerfis.Visibility = Visivel(_cfg.IlhaAbertaPerfis);
        BlocoCalendario.Visibility = Visivel(_cfg.IlhaAbertaCalendario);
        MontarAtalhos();
        MontarBandeja();
        if (_handle != IntPtr.Zero) DragAcceptFiles(_handle, _cfg.IlhaBandeja);
        AtualizarKdrop();
        BotaoMixer.Visibility = Visivel(_cfg.IlhaMixer);
        if (_expandida)
        {
            MontarSaidas();
            MontarMixer();
        }

        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AtualizarRelogio();
        AplicarMidia(false);
    }

    // O que a pilula mostra agora: a lista do dia a dia ou a do modo jogo.
    private bool MostraCpuTemp => _modoJogo ? _cfg.IlhaJogoCpu : _cfg.IlhaCpu;
    private bool MostraCpuUso => _modoJogo ? _cfg.IlhaJogoCpuUso : _cfg.IlhaCpuUso;
    private bool MostraGpuTemp => _modoJogo ? _cfg.IlhaJogoGpu : _cfg.IlhaGpu;
    private bool MostraGpuUso => _modoJogo ? _cfg.IlhaJogoGpuUso : _cfg.IlhaGpuUso;
    private bool MostraRam => _modoJogo ? _cfg.IlhaJogoRam : _cfg.IlhaRam;
    private bool MostraPotencia => _modoJogo ? _cfg.IlhaJogoPotencia : _cfg.IlhaPotencia;
    private bool MostraRelogio => _modoJogo ? _cfg.IlhaJogoRelogio : _cfg.IlhaRelogio;
    private bool MostraMusica => _modoJogo ? _cfg.IlhaJogoMusica : _cfg.IlhaMusica;

    /// <summary>
    /// Contorno suave, na cor do fundo da Kisland, nos textos que ficam direto
    /// sobre o fundo dela — titulo da musica, app, tempos, rodape, avisos.
    ///
    /// Com transparencia, esse fundo deixa passar o que esta atras, e no tema
    /// claro um texto cinza sobre uma janela escura atras some. Os cartoes nao
    /// precisam: sao opacos. O contorno devolve ao texto o fundo que ele
    /// perdeu, so em volta das letras, sem mudar o visual do resto.
    /// </summary>
    private void AplicarContorno()
    {
        System.Windows.Media.Effects.Effect? contorno = null;

        if (TransparenciaDaIlha)
        {
            var efeito = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = _cfg.IlhaPreta ? Colors.Black : ThemeService.CorDeFundo,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 1,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
            efeito.Freeze();
            contorno = efeito;
        }

        foreach (var texto in new UIElement[]
                 {
                     AppMidia, TituloMidia, ArtistaMidia, TempoAtual, TempoTotal,
                     Rodape, TituloAviso, ArtistaAviso, ValorAvisoVolume
                 })
            texto.Effect = contorno;
    }

    private static Visibility Visivel(bool sim) => sim ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------
    // Tela OLED

    /// <summary>Transparencia de fato: nem o tema Preto nem a Kisland preta a usam.</summary>
    private bool TransparenciaDaIlha => _cfg.TransparenciaAtiva && !_cfg.IlhaPreta;

    /// <summary>Sombra original da casca, guardada para voltar ao sair da Kisland preta.</summary>
    private System.Windows.Media.Effects.Effect? _sombra;

    /// <summary>
    /// Kisland preta: a paleta escura com superficies pretas pendurada nos
    /// recursos desta janela, por cima da do app. Tudo aqui usa
    /// DynamicResource, entao trocar os recursos repinta a Kisland inteira —
    /// e o resto do app continua no tema dele, claro ou escuro. Sem borda e
    /// sem sombra: no AMOLED, preto sobre preto, as duas so marcariam o
    /// contorno que a ideia e apagar.
    /// </summary>
    private void AplicarPreta()
    {
        _sombra ??= Casca.Effect;

        if (_cfg.IlhaPreta)
        {
            foreach (var (chave, pincel) in ThemeService.PaletaPreta())
                Resources[chave] = pincel;

            Casca.BorderBrush = Brushes.Transparent;
            Casca.Effect = null;
        }
        else
        {
            foreach (var chave in ThemeService.PaletaPreta().Keys)
                Resources.Remove(chave);

            Casca.SetResourceReference(Border.BorderBrushProperty, "Borda");
            Casca.Effect = _sombra;
        }
    }

    /// <summary>Passo do deslocamento contra marca; anda a cada 3 minutos.</summary>
    private int _passoDoPixel;

    // Deslocamentos em pixel, pequenos o bastante para ninguem notar e
    // variados o bastante para nenhum pixel ficar aceso sempre no mesmo ponto.
    private static readonly int[] DeslocamentoX = { 0, 2, -1, 1, -2, 0, 1, -1 };
    private static readonly int[] DeslocamentoY = { 0, 1, 2, 0, 1, 2, 1, 0 };

    private bool Mexendo => _cfg.IlhaProtegerTela && _cfg.IlhaMexerPixels;

    /// <summary>Quando o mouse esteve perto da Kisland, ou ela mostrou algo, pela ultima vez.</summary>
    private DateTime _vistaEm = DateTime.UtcNow;

    private bool _apagada;
    private bool _escondidaPorInatividade;

    /// <summary>Opacidade da Kisland escurecida: ainda legivel, bem menos acesa.</summary>
    private const double OpacidadeApagada = 0.35;

    /// <summary>
    /// Escurece sem o mouse por perto ha tantos minutos; volta inteira quando
    /// ele chega perto, quando ela abre ou mostra um aviso.
    /// </summary>
    private void ConferirBrilho()
    {
        var minutos = _cfg.IlhaEscurecerMinutos;
        var apagar = _cfg.IlhaProtegerTela && minutos > 0 && !_expandida && _aviso == Aviso.Nenhum &&
                     DateTime.UtcNow - _vistaEm >= TimeSpan.FromMinutes(minutos);

        if (apagar == _apagada) return;
        _apagada = apagar;

        var anim = new DoubleAnimation(apagar ? OpacidadeApagada : 1,
            TimeSpan.FromMilliseconds(apagar ? 900 : 180));
        Casca.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Acorda: volta o brilho na hora, sem esperar o proximo segundo.</summary>
    private void Acordar()
    {
        _vistaEm = DateTime.UtcNow;
        if (_apagada) ConferirBrilho();
    }

    /// <summary>
    /// Some depois de 10 minutos sem teclado nem mouse no PC inteiro, e volta
    /// no primeiro movimento. O PC parado com a tela acesa e o caso em que a
    /// Kisland mais marca a tela: horas do mesmo texto no mesmo lugar.
    /// </summary>
    private void ConferirInatividade()
    {
        // Com o Kdrop no meio de algo (um pedido esperando, arquivos chegando),
        // o PC "parado" e so a pessoa no celular: a Kisland fica.
        var kdropOcupado = _pedidoKdrop is not null || _recebendoKdrop is { Estado: EstadoDoPedido.Aceito };
        var parado = _cfg.IlhaProtegerTela && _cfg.IlhaEsconderParado && !kdropOcupado &&
                     TempoParado() >= TimeSpan.FromMinutes(10);

        if (parado && !_escondidaPorInatividade)
        {
            _escondidaPorInatividade = true;
            _expandida = false;
            _aviso = Aviso.Nenhum;
            MostrarEstado();
            Hide();
        }
        else if (!parado && _escondidaPorInatividade)
        {
            _escondidaPorInatividade = false;
            if (_ativa && !_escondidaPorTelaCheia)
            {
                Show();
                Posicionar();
                MostrarEstado();
            }
        }
    }

    private static TimeSpan TempoParado()
    {
        var info = new UltimaEntrada { Tamanho = (uint)Marshal.SizeOf<UltimaEntrada>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        var ms = unchecked((uint)Environment.TickCount - info.Quando);
        return TimeSpan.FromMilliseconds(ms);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UltimaEntrada
    {
        public uint Tamanho;
        public uint Quando;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref UltimaEntrada info);

    /// <summary>
    /// Distribui os cartoes ligados (CPU, GPU, RAM, disco e energia) nas duas
    /// fileiras conforme quantos sobraram, em vez de cada um ficar preso na
    /// sua: com CPU e RAM so, antes ficava um embaixo do outro, cada um com a
    /// linha inteira. Agora:
    /// - ate 3 ligados: uma fileira so, lado a lado;
    /// - 4: dois e dois;
    /// - 5: os dois de temperatura em cima e os tres de sistema embaixo.
    /// A ordem e sempre a mesma (CPU, GPU, RAM, disco, energia).
    /// </summary>
    private void OrganizarCartoes()
    {
        var todos = new FrameworkElement[] { CartaoCpu, CartaoGpu, CartaoRam, CartaoDisco, CartaoEnergia };

        foreach (var cartao in todos)
            (cartao.Parent as Panel)?.Children.Remove(cartao);

        var ligados = todos.Where(c => c.Visibility == Visibility.Visible).ToList();
        var naPrimeira = ligados.Count <= 3 ? ligados.Count : 2;

        for (var i = 0; i < ligados.Count; i++)
            (i < naPrimeira ? GradeTemperaturas : GradeSistema).Children.Add(ligados[i]);

        // Os desligados ficam guardados no fim, escondidos.
        foreach (var cartao in todos.Except(ligados))
            GradeSistema.Children.Add(cartao);

        AjustarFileira(GradeTemperaturas);
        AjustarFileira(GradeSistema);
    }

    /// <summary>
    /// Uma fileira de cartoes com so os escolhidos, dividindo a largura entre
    /// eles: sobrou um, ele ocupa a linha toda; nenhum, a fileira some. O
    /// espaco de 12 entre cartoes fica so entre vizinhos, nunca nas pontas.
    /// </summary>
    private static void AjustarFileira(System.Windows.Controls.Primitives.UniformGrid fileira)
    {
        var visiveis = fileira.Children.OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible)
            .ToList();

        fileira.Visibility = Visivel(visiveis.Count > 0);
        fileira.Columns = Math.Max(1, visiveis.Count);

        for (var i = 0; i < visiveis.Count; i++)
            visiveis[i].Margin = new Thickness(i == 0 ? 0 : 6, 0, i == visiveis.Count - 1 ? 0 : 6, 0);
    }

    /// <summary>Margens originais dos blocos da Kisland aberta, lidas do XAML na primeira vez.</summary>
    private Dictionary<FrameworkElement, Thickness>? _margens;

    /// <summary>
    /// Cada bloco da Kisland aberta tem espaco em cima para se separar do
    /// anterior. Escondido o primeiro, o seguinte herdaria um espaco sem
    /// ninguem acima — o primeiro visivel perde o espaco de cima.
    /// </summary>
    private void AjustarEspacos()
    {
        var blocos = new FrameworkElement[]
        {
            BlocoMidia, BlocoProgresso, SemMidia, BlocoCalendario, GradeTemperaturas,
            GradeSistema, BlocoVolume, GradePerfis, BlocoAtalhos, BlocoBandeja, BlocoRodape
        };

        _margens ??= blocos.ToDictionary(b => b, b => b.Margin);

        // Pedido do Kdrop esperando resposta, ou arquivo chegando arrastado:
        // so a bandeja, onde ele vai cair.
        if (_soBandeja || _soltandoNaBandeja)
            foreach (var bloco in blocos)
                if (!ReferenceEquals(bloco, BlocoBandeja)) bloco.Visibility = Visibility.Collapsed;

        var primeiro = true;
        foreach (var bloco in blocos)
        {
            if (bloco.Visibility != Visibility.Visible) continue;

            var m = _margens[bloco];
            bloco.Margin = primeiro ? new Thickness(m.Left, 0, m.Right, m.Bottom) : m;
            primeiro = false;
        }
    }

    /// <summary>
    /// Sem nada para mostrar, a pilula vira um circulo com o anel. O botao de
    /// Personalizacao sai junto: ao lado do anel ele seria o unico conteudo, e
    /// a ilha aberta continua com ele.
    ///
    /// Com um chat de IA escolhido, o anel fica sempre, no comeco da pilula, e
    /// vira o atalho para ele. No modo jogo nao: la so o FPS.
    /// </summary>
    private void AtualizarSemItens()
    {
        var algum = MidiaCompacta.Visibility == Visibility.Visible ||
                    ChipAlerta.Visibility == Visibility.Visible ||
                    ChipCpu.Visibility == Visibility.Visible ||
                    ChipGpu.Visibility == Visibility.Visible ||
                    ChipRam.Visibility == Visibility.Visible ||
                    ChipDisco.Visibility == Visibility.Visible ||
                    ChipPotencia.Visibility == Visibility.Visible ||
                    ChipVolume.Visibility == Visibility.Visible ||
                    ChipRelogio.Visibility == Visibility.Visible ||
                    ChipBandeja.Visibility == Visibility.Visible ||
                    ChipFps.Visibility == Visibility.Visible;

        var ia = _modoJogo ? null : AtalhoService.IaEscolhida(_cfg);
        var comBotao = algum && _cfg.IlhaBotaoPersonalizacao && !_modoJogo;

        BotaoAnel.Visibility = Visivel(!algum || ia is not null);
        BotaoAnel.IsHitTestVisible = ia is not null;
        BotaoAnel.ToolTip = ia is { } escolhida ? $"Abrir {escolhida.Nome}" : null;
        // A capa da musica nao tem margem propria, os chips tem.
        BotaoAnel.Margin = new Thickness(0, 0, algum && MidiaCompacta.Visibility == Visibility.Visible ? 8 : 0, 0);

        BotaoPersonalizarPilula.Visibility = Visivel(comBotao);
        // Com o anel sozinho, margens iguais: e o que faz a pilula virar circulo.
        Compacta.Margin = comBotao ? new Thickness(6, 0, 7, 0)
                        : algum ? new Thickness(6, 0, 8, 0)
                        : new Thickness(6, 0, 6, 0);
    }

    private void Anel_Click(object sender, RoutedEventArgs e) => AtalhoService.AbrirIa(_cfg);

    // ------------------------------------------------------------------
    // Posicao, monitor e foco

    /// <summary>
    /// Centro do topo da area util do monitor escolhido. Em pixels, pelo
    /// Windows, e nao pelas coordenadas do WPF: com monitores de escalas
    /// diferentes, as unidades do WPF mudam de tamanho no meio do caminho, e a
    /// conta de centralizar erraria justamente no monitor de destino.
    /// </summary>
    private void Posicionar()
    {
        if (_handle == IntPtr.Zero || ActualWidth <= 0 || _arrastando) return;

        try
        {
            var tela = MonitorDaIlha();
            var area = tela.WorkingArea;

            var escala = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (escala <= 0) escala = 1;

            var largura = (int)Math.Round(ActualWidth * escala);
            var x = area.Left + (area.Width - largura) / 2;
            var y = area.Top;

            if (Mexendo)
            {
                var i = _passoDoPixel % DeslocamentoX.Length;
                x += DeslocamentoX[i];
                y += DeslocamentoY[i];
            }

            SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0,
                         SemTamanho | SemOrdem | SemAtivarJanela);
        }
        catch (Exception ex)
        {
            Log.Erro("Ilha: posicionar", ex);
        }
    }

    private WF.Screen MonitorDaIlha()
    {
        var escolhido = string.IsNullOrEmpty(_cfg.IlhaMonitor)
            ? null
            : WF.Screen.AllScreens.FirstOrDefault(t => t.DeviceName == _cfg.IlhaMonitor);

        return escolhido ?? WF.Screen.PrimaryScreen ?? WF.Screen.AllScreens[0];
    }

    /// <summary>
    /// Arrastar leva a ilha para outro monitor, e ela vai para o topo dele,
    /// centralizada — e o lugar dela em qualquer tela. Sem arrasto, o toque
    /// vira clique: abre a ilha.
    /// </summary>
    private void Casca_Pressionada(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (EhControle(e.OriginalSource as DependencyObject)) return;

        GetWindowRect(_handle, out var antes);

        _arrastando = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // O botao pode ter sido solto antes de o arrasto comecar.
        }
        finally
        {
            _arrastando = false;
        }

        GetWindowRect(_handle, out var depois);
        var moveu = Math.Abs(depois.Esquerda - antes.Esquerda) > 4 ||
                    Math.Abs(depois.Topo - antes.Topo) > 4;

        e.Handled = true;

        if (!moveu)
        {
            Expandir();
            return;
        }

        var tela = WF.Screen.FromHandle(_handle);
        _cfg.IlhaMonitor = tela.Primary ? null : tela.DeviceName;
        _cfg.Salvar();

        // Acabou de soltar com o cursor em cima: abrir agora seria abrir sem
        // ninguem ter pedido. Abre so depois de o cursor sair e voltar.
        _exigirSaida = true;
        Posicionar();
    }

    /// <summary>
    /// O clique nasceu dentro de algo que precisa dele: botao, volume, perfil.
    /// Mesma conferencia do painel, que sobe a arvore visual a partir de onde
    /// o clique nasceu.
    /// </summary>
    private static bool EhControle(DependencyObject? origem)
    {
        while (origem is not null)
        {
            if (origem is System.Windows.Controls.Primitives.ButtonBase
                       or System.Windows.Controls.Primitives.Thumb
                       or System.Windows.Controls.Primitives.RepeatButton
                       or Slider)
                return true;

            if (origem is not Visual) return false;
            origem = VisualTreeHelper.GetParent(origem);
        }

        return false;
    }

    /// <summary>
    /// A ilha nunca toma o foco de quem esta usando o PC: clicar em tocar ou
    /// mexer no volume nao pode tirar o cursor do documento aberto. Tambem fica
    /// fora do Alt+Tab, como o painel.
    /// </summary>
    private void NaoRoubarFoco()
    {
        _handle = new WindowInteropHelper(this).Handle;
        var estilo = GetWindowLong(_handle, EstiloEstendido);
        SetWindowLong(_handle, EstiloEstendido, estilo | SemAtivar | JanelaDeFerramenta);
    }

    // ------------------------------------------------------------------
    // Abrir e fechar pelo cursor

    /// <summary>
    /// Onde esta o cursor, conferido dez vezes por segundo pela posicao na
    /// tela.
    ///
    /// Era por MouseEnter e MouseLeave, e nao abria. A ilha nunca e a janela
    /// ativa, e ao trocar de tamanho o WPF da o cursor como "fora" ate ele se
    /// mexer de novo — a ilha abria e o fechamento, logo em seguida, achava o
    /// cursor ausente e a recolhia. A posicao do cursor na tela nao depende de
    /// nada disso. E uma chamada ao Windows por decimo de segundo, so com a
    /// ilha visivel.
    /// </summary>
    private void Cursor_Tick()
    {
        if (!IsVisible || _arrastando) return;

        var agora = DateTime.UtcNow;
        var dentro = CursorEmCima();

        if (ConferirArrastoDeFora(dentro, agora)) return;

        // Mouse chegando perto (a 60 px) ja acende a Kisland escurecida: ela
        // fica legivel antes de o cursor chegar em cima.
        if (dentro || CursorEmCima(60)) Acordar();

        if (_exigirSaida)
        {
            if (dentro) return;
            _exigirSaida = false;
        }

        if (dentro)
        {
            _foraDesde = null;

            // No modo jogo ela nao abre sozinha: o mouse passa por ali o tempo
            // todo durante o jogo.
            if (!_expandida && !_modoJogo && _cfg.IlhaAbrirAoPassarMouse && !BotaoEsquerdoApertado())
            {
                _dentroDesde ??= agora;
                if (agora - _dentroDesde >= EsperaParaAbrir) Expandir();
            }

            return;
        }

        _dentroDesde = null;

        if (!_expandida) return;

        // Arrastando o volume o cursor pode sair da ilha sem a pessoa ter
        // terminado: so fecha depois de soltar.
        if (BotaoEsquerdoApertado())
        {
            _foraDesde = null;
            return;
        }

        // Com um pedido do Kdrop na tela, ela so fecha depois da resposta.
        if (_pedidoKdrop is not null)
        {
            _foraDesde = null;
            return;
        }

        _foraDesde ??= agora;
        if (agora - _foraDesde >= EsperaParaFechar) Recolher();
    }

    /// <param name="folga">Quanto alem da borda ainda conta, em unidades do WPF.</param>
    private bool CursorEmCima(double folga = 0)
    {
        if (!GetCursorPos(out var cursor) || Casca.ActualWidth <= 0) return false;

        try
        {
            var inicio = Casca.PointToScreen(new Point(-folga, -folga));
            var fim = Casca.PointToScreen(new Point(Casca.ActualWidth + folga, Casca.ActualHeight + folga));

            return cursor.X >= inicio.X && cursor.X <= fim.X &&
                   cursor.Y >= inicio.Y && cursor.Y <= fim.Y;
        }
        catch (InvalidOperationException)
        {
            // Sem janela na tela ainda.
            return false;
        }
    }

    private static bool BotaoEsquerdoApertado() => (GetAsyncKeyState(TeclaBotaoEsquerdo) & 0x8000) != 0;

    // ------------------------------------------------------------------
    // Arrastar arquivos de fora ate a bandeja

    /// <summary>Quanto o arrasto precisa ficar em cima da pilula para ela abrir a bandeja.</summary>
    private static readonly TimeSpan EsperaParaSoltar = TimeSpan.FromMilliseconds(250);

    private bool _botaoAntes;
    private bool _apertoDeFora;
    private DateTime? _arrastoDentroDesde;

    /// <summary>A Kisland aberta por um arrasto de fora: so a bandeja, esperando o arquivo.</summary>
    private bool _soltandoNaBandeja;

    private const string DicaBandejaPadrao = "Arraste arquivos até a Kisland para deixá-los aqui, à mão.";

    /// <summary>
    /// Arrastar um arquivo do Explorer ate a bandeja: antes, com a Kisland
    /// aberta, ir buscar o arquivo tirava o mouse dela e ela fechava; fechada,
    /// ela nao abria com o botao apertado. Agora, quem chega com o botao
    /// apertado desde fora (arrastando alguma coisa) abre a Kisland na hora,
    /// so com a bandeja, sem a espera normal de abrir.
    ///
    /// O arrastar do Windows nao avisa a Kisland de nada (ela recebe pelo
    /// caminho antigo, WM_DROPFILES, que so diz o que caiu, quando cai). Por
    /// isso o que vale e o botao e o cursor: botao apertado longe da Kisland
    /// e cursor chegando nela com o desenho de arrasto. O desenho separa o
    /// arquivo de quem so esta arrastando uma janela para o alto da tela (a
    /// seta comum) ou selecionando texto.
    /// </summary>
    /// <returns>Verdadeiro quando o arrasto cuidou deste decimo de segundo.</returns>
    private bool ConferirArrastoDeFora(bool dentro, DateTime agora)
    {
        var apertado = BotaoEsquerdoApertado();

        // O botao desceu agora: conta como "de fora" so se foi longe da Kisland.
        if (apertado && !_botaoAntes) _apertoDeFora = !dentro && !CursorEmCima(20);
        _botaoAntes = apertado;

        if (!apertado)
        {
            _apertoDeFora = false;
            _arrastoDentroDesde = null;
            if (_soltandoNaBandeja) TerminarSoltura(dentro);
            return false;
        }

        if (!_apertoDeFora || !_cfg.IlhaBandeja || _modoJogo || !_ativa) return false;

        if (!dentro)
        {
            _arrastoDentroDesde = null;
            return false;
        }

        Acordar();
        _foraDesde = null;
        _dentroDesde = null;

        if (_expandida) return true;

        _arrastoDentroDesde ??= agora;
        if (agora - _arrastoDentroDesde < EsperaParaSoltar || !CursorDeArrasto()) return true;

        _soltandoNaBandeja = true;
        Expandir();
        DicaBandeja.Text = "Solte aqui para deixar na bandeja.";
        DicaBandeja.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// Botao solto. Caiu na Kisland: ela fica aberta, agora inteira, mostrando
    /// o arquivo (quem o poe na bandeja e o WM_DROPFILES). Soltou fora,
    /// desistiu: ela fecha.
    /// </summary>
    private void TerminarSoltura(bool dentro)
    {
        _soltandoNaBandeja = false;
        DicaBandeja.Text = DicaBandejaPadrao;

        if (!dentro) Recolher();
        if (_pedidoKdrop is null) AplicarConfiguracao();
        else MontarBandeja();
    }

    /// <summary>
    /// O cursor e o de arrastar arquivo? Arrastando, o Windows troca a seta
    /// pelo desenho de copiar, mover ou "nao pode". Seta comum, texto, mao ou
    /// as setas de redimensionar sao de outra coisa. Sem conseguir ler o
    /// cursor, conta como arrasto.
    /// </summary>
    private static bool CursorDeArrasto()
    {
        var info = new InfoDoCursor { Tamanho = Marshal.SizeOf<InfoDoCursor>() };
        if (!GetCursorInfo(ref info) || info.Cursor == IntPtr.Zero) return true;

        foreach (var comum in CursoresComuns)
            if (info.Cursor == LoadCursor(IntPtr.Zero, comum))
                return false;

        return true;
    }

    /// <summary>IDC_ARROW, IBEAM, WAIT, CROSS, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, HAND, APPSTARTING.</summary>
    private static readonly IntPtr[] CursoresComuns =
        new[] { 32512, 32513, 32514, 32515, 32642, 32643, 32644, 32645, 32646, 32649, 32650 }
            .Select(id => (IntPtr)id).ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoDoCursor
    {
        public int Tamanho;
        public int Sinais;
        public IntPtr Cursor;
        public Ponto Posicao;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref InfoDoCursor info);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instancia, IntPtr nome);

    private void Expandir()
    {
        if (_expandida || !_ativa) return;

        _expandida = true;
        Acordar();
        _dentroDesde = null;
        _foraDesde = null;
        _aviso = Aviso.Nenhum;
        _fimDoAviso.Stop();

        SincronizarPerfil();
        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AplicarMidia(false);
        AplicarVolume(_volume.Nivel, _volume.Mudo, false);
        MontarSaidas();
        MontarMixer();
        MontarBandeja();
        AtualizarProgresso();

        MostrarEstado();

        Expandida.Opacity = 0;
        Expandida.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void Recolher()
    {
        if (!_expandida) return;

        _expandida = false;
        _foraDesde = null;
        MostrarEstado();
    }

    // ------------------------------------------------------------------
    // Relogio de um segundo

    private void Relogio_Tick()
    {
        _segundos++;

        // O aparelho de som pode ter mudado (fone ligado, troca no painel de
        // som). Conferir e barato, mas nao precisa ser a cada segundo.
        if (_segundos % 5 == 0)
        {
            var antes = _volume.Disponivel;
            _volume.Conferir();
            if (antes != _volume.Disponivel) AplicarConfiguracao();
        }

        // Mixer aberto: acompanha o que muda no Windows (app que abriu ou
        // fechou, volume mexido no mixer dele).
        if (_segundos % 2 == 0 && _expandida && MixerDeSom.Visibility == Visibility.Visible) MontarMixer();

        AtualizarFps();
        ConferirTelaCheia();
        ConferirInatividade();
        ConferirBrilho();
        ConferirPedidoKdrop();

        // Contra marca na tela: a cada 3 minutos, um passo de 1 ou 2 px.
        if (Mexendo && _segundos % 180 == 0)
        {
            _passoDoPixel++;
            Posicionar();
        }

        // Quem parou ha mais de tres minutos sai daqui: o servico so fica
        // sabendo que o tempo passou se alguem perguntar.
        _midia.Conferir();
        OuvirSaida();
        ConferirSilencio();
        AtualizarRelogio();

        if (_expandida) AtualizarProgresso();
    }

    private void AtualizarRelogio()
    {
        var agora = DateTime.Now;
        if (MostraRelogio) TextoRelogio.Text = agora.ToString("HH:mm");
        if (_cfg.IlhaData) TextoData.Text = $"{SemanasCurtas[(int)agora.DayOfWeek]} {agora.Day}";
        if (_cfg.IlhaAbertaCalendario) AtualizarCalendario();
    }

    private const string DesenhoRelogio = "M12,3 A9,9 0 1,1 11.99,3 Z M12,7 L12,12 L15,14";
    private const string DesenhoCalendario =
        "M6,5 L18,5 A3,3 0 0,1 21,8 L21,18 A3,3 0 0,1 18,21 L6,21 A3,3 0 0,1 3,18 L3,8 A3,3 0 0,1 6,5 Z " +
        "M3,10 L21,10 M8,3 L8,6 M16,3 L16,6";

    // Nomes escritos aqui, e nao pela cultura do Windows: em pt-BR ela abrevia
    // com ponto ("qui."), em minuscula, e muda conforme a versao do sistema.
    private static readonly string[] Meses =
    {
        "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO",
        "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"
    };
    private static readonly string[] Semanas =
    {
        "Domingo", "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado"
    };
    private static readonly string[] SemanasCurtas = { "Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb" };
    private static readonly string[] Iniciais = { "D", "S", "T", "Q", "Q", "S", "S" };

    /// <summary>
    /// O mini calendario da Kisland aberta. Refeito so quando o dia vira: no
    /// resto do tempo, nada muda nele.
    /// </summary>
    private void AtualizarCalendario()
    {
        var hoje = DateTime.Today;
        if (hoje == _diaDoCalendario) return;
        _diaDoCalendario = hoje;

        MesCalendario.Text = Meses[hoje.Month - 1];
        SemanaCalendario.Text = Semanas[(int)hoje.DayOfWeek];
        AnoCalendario.Text = hoje.Year.ToString();
        DiaCalendario.Text = hoje.Day.ToString();

        var grade = GradeCalendario;
        grade.Children.Clear();
        grade.RowDefinitions.Clear();
        grade.ColumnDefinitions.Clear();

        // A semana comeca no domingo, como nos calendarios daqui.
        var primeiro = new DateTime(hoje.Year, hoje.Month, 1);
        var vazios = (int)primeiro.DayOfWeek;
        var semanas = (vazios + DateTime.DaysInMonth(hoje.Year, hoje.Month) + 6) / 7;

        for (var c = 0; c < 7; c++)
            grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        for (var l = 0; l < semanas; l++)
            grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

        for (var c = 0; c < 7; c++)
        {
            var inicial = new TextBlock
            {
                Text = Iniciais[c],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            inicial.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            Grid.SetColumn(inicial, c);
            grade.Children.Add(inicial);
        }

        var inicio = primeiro.AddDays(-vazios);
        for (var i = 0; i < semanas * 7; i++)
        {
            var dia = inicio.AddDays(i);
            var linha = i / 7 + 1;
            var coluna = i % 7;

            if (dia == hoje)
            {
                var marca = new Ellipse { Width = 20, Height = 20 };
                marca.SetResourceReference(Shape.FillProperty, "Acento");
                Grid.SetRow(marca, linha);
                Grid.SetColumn(marca, coluna);
                grade.Children.Add(marca);
            }

            var numero = new TextBlock
            {
                Text = dia.Day.ToString(),
                FontSize = 11,
                FontWeight = dia == hoje ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (dia == hoje)
                numero.Foreground = Brushes.White;
            else
                numero.SetResourceReference(TextBlock.ForegroundProperty,
                    dia.Month == hoje.Month ? "Texto" : "TextoMuitoFraco");

            // Dias do mes vizinho, so para fechar a semana: bem apagados.
            if (dia.Month != hoje.Month) numero.Opacity = 0.55;

            Grid.SetRow(numero, linha);
            Grid.SetColumn(numero, coluna);
            grade.Children.Add(numero);
        }
    }

    // ------------------------------------------------------------------
    // Atalhos

    /// <summary>
    /// Os atalhos da Kisland aberta: o chat de IA primeiro, com o anel no
    /// lugar do icone, e depois os apps e sites, na ordem escolhida.
    /// </summary>
    private void MontarAtalhos()
    {
        GradeAtalhos.Children.Clear();

        if (AtalhoService.IaEscolhida(_cfg) is { } ia)
            GradeAtalhos.Children.Add(BotaoDeAtalho(ia.Nome, AtalhoService.MarcaDaIa, ehIa: true));

        foreach (var atalho in _cfg.IlhaAtalhos.Take(AtalhoService.Maximo))
            GradeAtalhos.Children.Add(BotaoDeAtalho(atalho.Nome, atalho.Destino, ehIa: false));

        // Pelo menos cinco colunas: com poucos atalhos eles ficam juntos a
        // esquerda, no tamanho de sempre, em vez de esticados pela largura.
        GradeAtalhos.Columns = Math.Max(GradeAtalhos.Children.Count, 5);
        BlocoAtalhos.Visibility = Visivel(_cfg.IlhaAbertaAtalhos && GradeAtalhos.Children.Count > 0);
    }

    private Button BotaoDeAtalho(string nome, string destino, bool ehIa)
    {
        var circulo = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        circulo.SetResourceReference(Border.BackgroundProperty, "Chip");

        if (ehIa)
        {
            var anel = new Grid { Width = 24, Height = 24 };
            var fora = new Ellipse { Width = 24, Height = 24, StrokeThickness = 1, Opacity = 0.3 };
            var dentro = new Ellipse { Width = 15, Height = 15, StrokeThickness = 2.6 };
            fora.SetResourceReference(Shape.StrokeProperty, "Acento");
            dentro.SetResourceReference(Shape.StrokeProperty, "Acento");
            anel.Children.Add(fora);
            anel.Children.Add(dentro);
            circulo.Child = anel;
        }
        else if (AtalhoService.Icone(destino) is { } icone)
        {
            var imagem = new Image { Source = icone, Width = 22, Height = 22 };
            RenderOptions.SetBitmapScalingMode(imagem, BitmapScalingMode.HighQuality);
            circulo.Child = imagem;
        }
        else
        {
            var letra = new TextBlock
            {
                Text = Inicial(nome),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            letra.SetResourceReference(TextBlock.ForegroundProperty, "Acento");
            circulo.Child = letra;
        }

        var rotulo = new TextBlock
        {
            Text = nome,
            FontSize = 10.5,
            Margin = new Thickness(0, 5, 0, 0),
            MaxWidth = 64,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        rotulo.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        var conteudo = new StackPanel();
        conteudo.Children.Add(circulo);
        conteudo.Children.Add(rotulo);

        var botao = new Button
        {
            Style = (Style)FindResource("BotaoAtalho"),
            Content = conteudo,
            Tag = destino,
            ToolTip = ehIa ? $"Abrir {nome}" : nome
        };
        botao.Click += Atalho_Click;
        return botao;
    }

    private static string Inicial(string nome)
    {
        var letra = nome.FirstOrDefault(char.IsLetterOrDigit);
        return letra == default ? "?" : char.ToUpperInvariant(letra).ToString();
    }

    private void Atalho_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string destino }) return;

        Recolher();
        if (destino == AtalhoService.MarcaDaIa) AtalhoService.AbrirIa(_cfg);
        else AtalhoService.Abrir(destino);
    }

    /// <summary>
    /// Some, fica, ou entra no modo jogo, conforme o que esta em tela cheia e
    /// onde.
    ///
    /// Tela cheia em outro monitor nao e motivo para sumir deste — antes era,
    /// e um jogo no segundo monitor apagava a Kisland do primeiro. Na mesma
    /// tela, se e o jogo do contador de FPS em janela sem borda, ela fica,
    /// enxuta, so com o FPS. Em tela cheia exclusiva nao ha o que fazer: nada
    /// aparece por cima, e o Windows nem diz em qual monitor ela esta.
    /// </summary>
    private void ConferirTelaCheia()
    {
        var estado = TelaCheia.Consultar(_handle);
        var meuMonitor = TelaCheia.MonitorDe(_handle);

        // Exclusiva: o Windows nao diz o monitor, mas o jogo do contador tem
        // janela, e a janela diz. Sem jogo conhecido, vale o lado seguro.
        var aqui = estado.Exclusiva
            ? _jogo is not { } j || TelaCheia.MonitorDe(j.Janela) == meuMonitor
            : estado.Janela != IntPtr.Zero && estado.Monitor == meuMonitor;

        bool ehOJogo;
        if (!_cfg.IlhaFps || _jogo is not { } jogo)
            ehOJogo = false;
        else if (estado.Exclusiva)
            // So com a opcao ligada, e so se quem esta na frente e o jogo.
            ehOJogo = _cfg.IlhaFicarEmTelaCheia && ProcessoDaJanela(PrimeiroPlano()) == jogo.Pid;
        else
            ehOJogo = estado.Janela != IntPtr.Zero && ProcessoDaJanela(estado.Janela) == jogo.Pid;

        var modoJogo = aqui && ehOJogo;
        var esconder = aqui && !modoJogo && _cfg.IlhaEsconderEmTelaCheia;

        // O jogo, ao tomar a tela, costuma passar a frente de tudo — inclusive
        // de janelas "sempre por cima". Reafirmar a cada segundo e o que mantem
        // a Kisland visivel por cima dele. Sem ativar: o foco continua no jogo.
        if (modoJogo && IsVisible)
            SetWindowPos(_handle, PorCimaDeTudo, 0, 0, 0, 0, SemTamanho | SemMover | SemAtivarJanela);

        if (modoJogo != _modoJogo)
        {
            _modoJogo = modoJogo;
            if (modoJogo)
            {
                _expandida = false;
                _aviso = Aviso.Nenhum;
            }

            AplicarConfiguracao();
        }

        if (esconder && IsVisible)
        {
            _escondidaPorTelaCheia = true;
            _expandida = false;
            _aviso = Aviso.Nenhum;
            MostrarEstado();
            Hide();
        }
        else if (!esconder && _escondidaPorTelaCheia)
        {
            _escondidaPorTelaCheia = false;

            // Escondida tambem pelo PC parado: quem a traz de volta e o
            // primeiro movimento, em ConferirInatividade.
            if (!_escondidaPorInatividade)
            {
                Show();
                Posicionar();
                MostrarEstado();
            }
        }
    }

    /// <summary>
    /// Uma vez por segundo, com o contador ligado: colhe os quadros de cada
    /// programa e ve se algum deles e um jogo. A sessao do Windows so existe
    /// enquanto o contador esta ligado.
    /// </summary>
    private void AtualizarFps()
    {
        if (!_cfg.IlhaFps)
        {
            if (_fps.Rodando) _fps.Parar();
            if (_jogo is not null)
            {
                _jogo = null;
                AplicarConfiguracao();
            }
            return;
        }

        // Recusado (sem privilegio, por exemplo), tenta de novo a cada meio
        // minuto em vez de a cada segundo. Mexer na Personalizacao zera a
        // espera, para quem acabou de ligar nao ficar esperando.
        if (!_fps.Rodando)
        {
            if (_segundos < _tentarFpsEm) return;

            if (!_fps.Iniciar())
            {
                _tentarFpsEm = _segundos + 30;
                return;
            }
        }

        var jogo = DetectorDeJogo.Escolher(_fps.Colher());
        var mudou = (jogo is null) != (_jogo is null);

        if (jogo is not null)
        {
            var mesmo = _jogo?.Pid == jogo.Pid && _fpsMostrado > 0;
            _fpsMostrado = mesmo && Math.Abs(jogo.Fps - _fpsMostrado) < _fpsMostrado * 0.15
                ? _fpsMostrado * 0.5 + jogo.Fps * 0.5
                : jogo.Fps;
            jogo = jogo with { Fps = (int)Math.Round(_fpsMostrado) };
        }
        else
        {
            _fpsMostrado = 0;
        }

        _jogo = jogo;

        if (jogo is not null)
        {
            TextoFps.Text = $"{jogo.Fps} FPS";
            ChipFps.ToolTip = jogo.Nome;
        }

        if (mudou) AplicarConfiguracao();
    }

    private static IntPtr PrimeiroPlano() => GetForegroundWindow();

    private static int ProcessoDaJanela(IntPtr janela)
    {
        GetWindowThreadProcessId(janela, out var pid);
        return pid;
    }

    /// <summary>
    /// Marca quando a midia foi ouvida pela ultima vez.
    ///
    /// Antes valia so o que o player declarava, e isso segurava a ilha: varios
    /// players — abas de navegador, mensagens de audio, video que terminou —
    /// continuam dizendo "tocando" muito depois de calar. Agora a prova e o
    /// som saindo de verdade pela saida de audio, lido no medidor de pico do
    /// Windows, junto com o player dizer que toca. Faltando um dos dois, conta
    /// como silencio.
    ///
    /// Sem medidor (nenhum aparelho de som), sobra a palavra do player.
    /// </summary>
    private void OuvirSaida()
    {
        if (_midia.Atual is not { Tocando: true }) return;

        var pico = _volume.Pico();
        if (pico is null || pico > LimiarDeSom) _somDaMidiaEm = DateTime.UtcNow;
    }

    /// <summary>Abaixo disto e silencio: cerca de -60 dB, o chiado de fundo de uma faixa parada.</summary>
    private const float LimiarDeSom = 0.001f;

    /// <summary>
    /// Tres minutos sem som, o player sai da ilha: musica calada ha tanto tempo
    /// deixou de ser "tocando agora". Volta quando o player muda de estado
    /// (pausa vira play, ou o contrario) ou anuncia outra faixa.
    /// </summary>
    private void ConferirSilencio()
    {
        if (_faixaDispensada is not null) return;
        if (_midia.Atual is not { } estado) return;
        if (DateTime.UtcNow - _somDaMidiaEm < SilencioQueLimpa) return;

        _faixaDispensada = Chave(estado);
        _tocandoAoDispensar = estado.Tocando;
        AplicarMidia(false);
    }

    // ------------------------------------------------------------------
    // Estados visiveis

    /// <summary>Um estado visivel por vez; a janela se ajusta ao que sobrou.</summary>
    private void MostrarEstado()
    {
        var aviso = _expandida ? Aviso.Nenhum : _aviso;

        Expandida.Visibility = Visivel(_expandida);
        AvisoVolume.Visibility = Visivel(aviso == Aviso.Volume);
        AvisoFaixa.Visibility = Visivel(aviso == Aviso.Faixa);
        Compacta.Visibility = Visivel(!_expandida && aviso == Aviso.Nenhum);

        Casca.CornerRadius = new CornerRadius(_expandida ? 16 : aviso == Aviso.Faixa ? 28 : 22);

        // Animacoes paradas quando ninguem as ve.
        var pilulaNaTela = _ativa && !_escondidaPorTelaCheia && !_escondidaPorInatividade &&
                           Compacta.Visibility == Visibility.Visible;

        if (pilulaNaTela && MidiaVisivel() is { Tocando: true } && MostraMusica) AndarEqualizador();
        else PararEqualizador();

        if (pilulaNaTela && BotaoAnel.Visibility == Visibility.Visible) AndarAnel();
        else PararAnel();
    }

    private void MostrarAviso(Aviso aviso, TimeSpan duracao)
    {
        if (!_ativa || _expandida || _escondidaPorTelaCheia || _escondidaPorInatividade) return;

        _aviso = aviso;
        Acordar();
        _fimDoAviso.Stop();
        _fimDoAviso.Interval = duracao;
        _fimDoAviso.Start();

        MostrarEstado();
    }

    private void EncerrarAviso()
    {
        _aviso = Aviso.Nenhum;
        MostrarEstado();
    }

    // ------------------------------------------------------------------
    // Sensores

    private void AplicarSensores(Snapshot s)
    {
        TextoCpu.Text = Rotulo("CPU", MostraCpuTemp ? s.CpuTemp : null, MostraCpuUso ? s.CpuUso : null);
        TextoGpu.Text = Rotulo("GPU", MostraGpuTemp ? s.GpuTemp : null, MostraGpuUso ? s.GpuUso : null);
        TextoRam.Text = s.RamUso is { } ram ? $"RAM {ram:F0}%" : "RAM --";
        TextoDisco.Text = s.DiscoTempMaior is > 0 ? $"Disco {s.DiscoTempMaior:F0}°" : "Disco --";
        TextoPotencia.Text = s.WattsTotais > 0 ? $"{s.WattsTotais:F0} W" : "-- W";

        if (_alertas.Atual is { } alerta)
        {
            TextoAlerta.Text = $"{alerta.Componente} {alerta.Atual:F0}°";
            ChipAlerta.Visibility = Visibility.Visible;
            ChipAlerta.ToolTip = $"{alerta.Titulo}\n{alerta.Detalhe}";
        }
        else
        {
            ChipAlerta.Visibility = Visibility.Collapsed;
        }

        AtualizarSemItens();
        MostrarEstado();

        if (!_expandida) return;

        ValorCpu.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0}" : "--";
        ValorGpu.Text = s.GpuTemp is > 0 ? $"{s.GpuTemp:F0}" : "--";
        RotuloCpu.Text = s.CpuUso is { } cpu ? $"CPU · {cpu:F0}% de carga" : "CPU";
        RotuloGpu.Text = s.GpuUso is { } gpu ? $"GPU · {gpu:F0}% de carga" : "GPU";

        // Acima do limite que a pessoa definiu, o numero fica laranja, como o
        // anel do painel.
        ValorCpu.SetResourceReference(TextBlock.ForegroundProperty,
            s.CpuTemp is { } tc && tc >= _cfg.LimiteCpu ? "Alerta" : "Texto");
        ValorGpu.SetResourceReference(TextBlock.ForegroundProperty,
            s.GpuTemp is { } tg && tg >= _cfg.LimiteGpu ? "Alerta" : "Texto");

        ValorRam.Text = s.RamUso is { } r ? $"{r:F0}" : "--";
        RotuloRam.Text = s is { RamUsadaGb: { } usada, RamTotalGb: { } total }
            ? $"RAM · {usada:F1} de {total:F0} GB"
            : "RAM";
        ValorDisco.Text = s.DiscoTempMaior is > 0 ? $"{s.DiscoTempMaior:F0}" : "--";
        ValorDisco.SetResourceReference(TextBlock.ForegroundProperty,
            s.DiscoTempMaior is { } td && td >= _cfg.LimiteDisco ? "Alerta" : "Texto");
        ValorPotencia.Text = s.WattsTotais > 0 ? $"{s.WattsTotais:F0}" : "--";

        Rodape.Text = _jogo is { } jogo ? $"{jogo.Nome} · {jogo.Fps} FPS"
                    : s.SensoresDeBaixoNivel ? "Sensores ativos"
                    : "Sensores de baixo nível indisponíveis";
    }

    /// <summary>"CPU 62°", "CPU 34%" ou "CPU 62° · 34%", conforme o que esta ligado.</summary>
    private static string Rotulo(string peca, float? temperatura, float? uso)
    {
        var partes = new List<string>(2);
        if (temperatura is > 0) partes.Add($"{temperatura:F0}°");
        if (uso is { } u) partes.Add($"{u:F0}%");

        return partes.Count == 0 ? $"{peca} --" : $"{peca} {string.Join(" · ", partes)}";
    }

    // ------------------------------------------------------------------
    // Midia

    private static string Chave(EstadoDeMidia e) => e.Origem + "\u0001" + e.Titulo + "\u0001" + e.Artista;

    /// <summary>O que a ilha mostra: o que toca, menos o que ja foi dispensado por silencio.</summary>
    private EstadoDeMidia? MidiaVisivel()
    {
        var estado = _midia.Atual;
        if (estado is null) return null;

        return _faixaDispensada is not null && Chave(estado) == _faixaDispensada &&
               estado.Tocando == _tocandoAoDispensar
            ? null
            : estado;
    }

    private void AplicarMidia(bool veioDeAviso)
    {
        var bruto = _midia.Atual;

        // Faixa nova, ou o player mudou de estado depois de dispensado: volta
        // a aparecer, com mais tres minutos de prazo. Aviso repetido do mesmo
        // estado nao conta — e eles chegam a toda hora.
        if (bruto is null)
        {
            _faixaDispensada = null;
            _somDaMidiaEm = DateTime.UtcNow;
        }
        else if (_faixaDispensada is not null &&
                 (_faixaDispensada != Chave(bruto) || bruto.Tocando != _tocandoAoDispensar))
        {
            _faixaDispensada = null;
            _somDaMidiaEm = DateTime.UtcNow;
        }
        else if (_faixaDispensada is null && _ultimaFaixa is not null && _ultimaFaixa != Chave(bruto))
        {
            _somDaMidiaEm = DateTime.UtcNow;
        }

        var estado = MidiaVisivel();

        var mostrar = estado is not null && MostraMusica;
        MidiaCompacta.Visibility = Visivel(mostrar);

        PausadoPequeno.Visibility = Visivel(estado is { Tocando: false });
        Equalizador.Visibility = Visivel(estado is not { Tocando: false });

        PorCapa(CapaPequena, NotaPequena, estado?.Capa);
        PorCapa(CapaGrande, NotaGrande, estado?.Capa);

        var player = _cfg.IlhaAbertaPlayer;
        BlocoMidia.Visibility = Visivel(player && estado is not null);
        BlocoProgresso.Visibility = Visivel(player && estado is { Duracao.TotalSeconds: > 0 });
        SemMidia.Visibility = Visivel(player && estado is null);
        AjustarEspacos();

        if (estado is not null)
        {
            TituloMidia.Text = estado.Titulo;
            ArtistaMidia.Text = estado.Artista;

            var app = string.IsNullOrEmpty(estado.App) ? "" : $" · {estado.App}";
            AppMidia.Text = (estado.Tocando ? "Tocando agora" : "Pausado") + app;

            IconeTocar.Data = Geometry.Parse(estado.Tocando
                ? "M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z"
                : "M7,4 L7,20 L20,12 Z");
        }

        // Faixa nova, tocando, e nao a primeira leitura: a pilula mostra o
        // nome por tres segundos.
        var chave = estado is null ? null : Chave(estado);
        if (veioDeAviso && chave is not null && _ultimaFaixa is not null && chave != _ultimaFaixa &&
            estado!.Tocando && _cfg.IlhaAvisoDeFaixa)
        {
            TituloAviso.Text = estado.Titulo;
            ArtistaAviso.Text = estado.Artista;
            PorCapa(CapaAviso, NotaAviso, estado.Capa);
            MostrarAviso(Aviso.Faixa, TimeSpan.FromSeconds(3));
        }

        if (chave is not null) _ultimaFaixa = chave;

        AtualizarSemItens();
        if (_expandida) AtualizarProgresso();
        MostrarEstado();
    }

    /// <summary>
    /// Capa no fundo do quadro, pelo pincel: assim o canto arredondado do
    /// quadro recorta a imagem, o que um Image dentro dele nao faria.
    /// </summary>
    private static void PorCapa(Border quadro, System.Windows.Shapes.Path nota, ImageSource? capa)
    {
        if (capa is null)
        {
            quadro.SetResourceReference(Border.BackgroundProperty, "Chip");
            nota.Visibility = Visibility.Visible;
            return;
        }

        quadro.Background = new ImageBrush(capa) { Stretch = Stretch.UniformToFill };
        nota.Visibility = Visibility.Collapsed;
    }

    private void AtualizarProgresso()
    {
        if (MidiaVisivel() is not { Duracao.TotalSeconds: > 0 } estado) return;

        var agora = estado.PosicaoAgora();
        var fracao = Math.Clamp(agora.TotalSeconds / estado.Duracao.TotalSeconds, 0, 1);

        ProgressoFeito.Width = new GridLength(fracao, GridUnitType.Star);
        ProgressoFalta.Width = new GridLength(1 - fracao, GridUnitType.Star);

        TempoAtual.Text = Tempo(agora);
        TempoTotal.Text = Tempo(estado.Duracao);
    }

    private static string Tempo(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void AndarEqualizador()
    {
        if (_equalizadorAndando) return;
        _equalizadorAndando = true;

        var duracoes = new[] { 420, 520, 380, 470 };
        var i = 0;

        foreach (var barra in Equalizador.Children.OfType<Rectangle>())
        {
            var escala = new ScaleTransform(1, 1);
            barra.RenderTransform = escala;

            var anim = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(duracoes[i++ % duracoes.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Quinze quadros por segundo bastam para barrinhas de 16 px, e
            // custam um quarto do padrao de sessenta.
            Timeline.SetDesiredFrameRate(anim, 15);
            escala.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void PararEqualizador()
    {
        if (!_equalizadorAndando) return;
        _equalizadorAndando = false;

        foreach (var barra in Equalizador.Children.OfType<Rectangle>())
            if (barra.RenderTransform is ScaleTransform escala)
                escala.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>O anel respira devagar: so estetica, a doze quadros por segundo.</summary>
    private void AndarAnel()
    {
        if (_anelAndando) return;
        _anelAndando = true;

        var escala = new ScaleTransform(1, 1);
        Anel.RenderTransform = escala;

        var anim = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(1800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(anim, 12);

        escala.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        escala.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void PararAnel()
    {
        if (!_anelAndando) return;
        _anelAndando = false;

        if (Anel.RenderTransform is ScaleTransform escala)
        {
            escala.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            escala.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    private void TocarPausar_Click(object sender, RoutedEventArgs e) => _ = _midia.TocarPausar();
    private void Proxima_Click(object sender, RoutedEventArgs e) => _ = _midia.Proxima();
    private void Anterior_Click(object sender, RoutedEventArgs e) => _ = _midia.Anterior();

    // ------------------------------------------------------------------
    // Volume

    // Alto-falante pequeno e bem arredondado, com ondas curtas e leves. O
    // desenho anterior tinha o corpo grande e ondas largas: em 12 px, com
    // traco grosso, pesava mais que os outros icones da pilula.
    private const string Corpo =
        "M5,9.9 C5,9.4 5.4,9 5.9,9 L8,9 L11,6.5 C11.6,6 12.5,6.4 12.5,7.2 " +
        "L12.5,16.8 C12.5,17.6 11.6,18 11,17.5 L8,15 L5.9,15 C5.4,15 5,14.6 5,14.1 Z";
    private const string OndaPerto = " M15.6,10.2 C16.4,11.2 16.4,12.8 15.6,13.8";
    private const string OndaLonge = " M17.9,8.3 C19.7,10.4 19.7,13.6 17.9,15.7";
    private const string Corte = " M16.2,10.6 L19,13.4 M19,10.6 L16.2,13.4";

    private static string DesenhoDoVolume(int nivel, bool mudo) =>
        mudo || nivel == 0 ? Corpo + Corte
        : nivel < 50 ? Corpo + OndaPerto
        : Corpo + OndaPerto + OndaLonge;

    /// <summary>
    /// Desenha sempre na mesma moldura, sem esticar pelo desenho. Esticado
    /// (Uniform), o icone mudava de tamanho com o nivel: sem a onda de fora o
    /// desenho e mais estreito, e o alto-falante crescia para ocupar o espaco.
    ///
    /// A moldura e a do icone completo (x de 5 a 20, na grade de 24), e nao a
    /// grade inteira: com a grade inteira sobrava borda vazia e o icone saia
    /// bem menor que os outros da Kisland.
    /// </summary>
    private static void DesenharVolume(System.Windows.Shapes.Path alvo, string desenho)
    {
        var tamanho = double.IsNaN(alvo.Width) ? 16 : alvo.Width;
        var escala = tamanho / 15.0;

        var geometria = Geometry.Parse(desenho).CloneCurrentValue();
        var transformacao = new TransformGroup();
        transformacao.Children.Add(new TranslateTransform(-5, -4.5));
        transformacao.Children.Add(new ScaleTransform(escala, escala));
        geometria.Transform = transformacao;
        geometria.Freeze();

        alvo.Stretch = Stretch.None;
        alvo.Data = geometria;
    }

    private void AplicarVolume(int nivel, bool mudo, bool veioDeAviso)
    {
        var desenho = DesenhoDoVolume(nivel, mudo);
        DesenharVolume(IconeVolumeChip, desenho);
        DesenharVolume(IconeAvisoVolume, desenho);
        DesenharVolume(IconeMudo, desenho);

        var texto = mudo ? "Mudo" : $"{nivel}%";
        TextoVolume.Text = texto;
        ValorVolume.Text = texto;
        ValorAvisoVolume.Text = mudo ? "0" : $"{nivel}";

        var fracao = mudo ? 0 : Math.Clamp(nivel / 100.0, 0, 1);
        AvisoVolumeCheio.Width = new GridLength(fracao, GridUnitType.Star);
        AvisoVolumeVazio.Width = new GridLength(1 - fracao, GridUnitType.Star);

        _ajustandoVolume = true;
        SliderVolume.Value = nivel;
        _ajustandoVolume = false;

        // Quem mexeu foi a propria ilha: a pessoa esta olhando o controle, nao
        // precisa de aviso.
        if (veioDeAviso && _cfg.IlhaAvisoDeVolume && !_volume.UltimaFoiMinha)
            MostrarAviso(Aviso.Volume, TimeSpan.FromSeconds(2));
    }

    private void SliderVolume_Alterado(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ajustandoVolume || !IsInitialized) return;

        _volume.Definir((int)e.NewValue);
        ValorVolume.Text = $"{e.NewValue:F0}%";
    }

    private void Mudo_Click(object sender, RoutedEventArgs e) => _volume.AlternarMudo();

    // ------------------------------------------------------------------
    // Roda do mouse e clique do meio

    private int _rodaAcumulada;

    /// <summary>Quanto cada "clique" da roda muda o volume: o mesmo passo do Windows.</summary>
    private const int PassoDaRoda = 2;

    private IntPtr Mensagem(IntPtr janela, int mensagem, IntPtr w, IntPtr l, ref bool tratada)
    {
        switch (mensagem)
        {
            case RodaDoMouse:
                tratada = GirarRoda((short)((w.ToInt64() >> 16) & 0xFFFF));
                break;

            case BotaoDoMeioSolto:
                if (_cfg.IlhaRodaMudaVolume && !_modoJogo && _midia.Atual is not null)
                {
                    _ = _midia.TocarPausar();
                    tratada = true;
                }
                break;

            case ArquivosSoltos:
                ReceberArquivos(w);
                tratada = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// A roda em cima da Kisland muda o volume, de 2 em 2%. O giro chega em
    /// partes de 120 por clique; touchpads mandam pedacos menores, que se
    /// somam ate dar um passo. No modo jogo nao: o mouse passa por ali no meio
    /// da partida.
    /// </summary>
    private bool GirarRoda(int giro)
    {
        if (!_cfg.IlhaRodaMudaVolume || _modoJogo || !_volume.Disponivel) return false;

        _rodaAcumulada += giro;
        var passos = _rodaAcumulada / 120;
        if (passos == 0) return true;
        _rodaAcumulada -= passos * 120;

        // Em cima de uma linha do mixer, a roda muda o volume daquele app.
        if (_expandida && LinhaSobOCursor() is { } linha)
        {
            linha.Controle.Value = Math.Clamp(linha.Controle.Value + passos * PassoDaRoda, 0, 100);
            return true;
        }

        var nivel = Math.Clamp(_volume.Nivel + passos * PassoDaRoda, 0, 100);

        // Subir o volume com o som mudo tira o mudo, como no Windows.
        var mudo = _volume.Mudo;
        if (mudo && passos > 0)
        {
            _volume.AlternarMudo();
            mudo = false;
        }

        _volume.Definir(nivel);

        // Mudanca feita pela propria Kisland nao vira aviso sozinha. Fechada,
        // ela mostra o aviso de volume aqui, para a roda ter resposta na hora.
        AplicarVolume(nivel, mudo, false);
        if (!_expandida) MostrarAviso(Aviso.Volume, TimeSpan.FromSeconds(1.5));

        return true;
    }

    // ------------------------------------------------------------------
    // Mixer: o volume de cada app

    /// <summary>Uma linha do mixer, guardada no Tag da grade dela.</summary>
    private sealed record LinhaDoMixer(string Chave, Slider Controle, TextBlock Valor, FrameworkElement Icone, TextBlock Nome);

    private readonly Dictionary<string, LinhaDoMixer> _linhasDoMixer = new();
    private readonly Dictionary<string, bool> _mudoNoMixer = new();
    private string _assinaturaDoMixer = "";
    private bool _ajustandoMixer;

    private void Mixer_Click(object sender, RoutedEventArgs e)
    {
        _cfg.IlhaMixerAberto = !_cfg.IlhaMixerAberto;
        _cfg.Salvar();
        MontarMixer();
    }

    /// <summary>
    /// Abre, fecha ou atualiza o mixer. Com os mesmos apps de antes, so os
    /// valores mudam — refazer as linhas a cada 2 s tiraria o controle da mao
    /// de quem esta arrastando.
    /// </summary>
    private void MontarMixer()
    {
        var aberto = _cfg.IlhaMixer && _cfg.IlhaMixerAberto && _volume.Disponivel;
        MixerDeSom.Visibility = Visivel(aberto);
        IconeMixer.SetResourceReference(Shape.StrokeProperty, aberto ? "Acento" : "Icone");

        if (!aberto)
        {
            AppsDoMixer.Children.Clear();
            _linhasDoMixer.Clear();
            _assinaturaDoMixer = "";
            return;
        }

        var apps = MixerService.Apps();
        MixerVazio.Visibility = Visivel(apps.Count == 0);

        var assinatura = string.Join("|", apps.Select(a => a.Chave));
        if (assinatura != _assinaturaDoMixer)
        {
            _assinaturaDoMixer = assinatura;
            AppsDoMixer.Children.Clear();
            _linhasDoMixer.Clear();
            foreach (var app in apps)
                AppsDoMixer.Children.Add(LinhaDoApp(app));
        }

        _ajustandoMixer = true;
        foreach (var app in apps)
        {
            if (!_linhasDoMixer.TryGetValue(app.Chave, out var linha)) continue;

            _mudoNoMixer[app.Chave] = app.Mudo;
            linha.Icone.Opacity = app.Mudo ? 0.4 : 1;
            linha.Nome.SetResourceReference(TextBlock.ForegroundProperty, app.Ativo ? "Texto" : "TextoFraco");

            // Quem esta arrastando manda no controle ate soltar.
            if (linha.Controle.IsMouseCaptureWithin) continue;
            linha.Controle.Value = app.Nivel;
            linha.Valor.Text = app.Mudo ? "Mudo" : $"{app.Nivel}%";
        }
        _ajustandoMixer = false;
    }

    private Grid LinhaDoApp(AppDeSom app)
    {
        var grade = new Grid { Margin = new Thickness(0, 6, 0, 0), Background = Brushes.Transparent };
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

        // Icone do app; tocar silencia so ele.
        var quadro = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Child = IconeDoApp(app)
        };
        quadro.SetResourceReference(Border.BackgroundProperty, "Chip");

        var botao = new Button
        {
            Style = (Style)FindResource("BotaoSemMoldura"),
            Content = quadro,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Silenciar ou voltar o som de {app.Nome}"
        };
        var chave = app.Chave;
        botao.Click += (_, _) =>
        {
            _mudoNoMixer.TryGetValue(chave, out var mudo);
            MixerService.DefinirMudo(chave, !mudo);
            MontarMixer();
        };

        var nome = new TextBlock
        {
            Text = app.Nome,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = app.Nome
        };
        nome.SetResourceReference(TextBlock.ForegroundProperty, app.Ativo ? "Texto" : "TextoFraco");

        var controle = new Slider
        {
            Style = (Style)FindResource("SliderIlha"),
            Minimum = 0,
            Maximum = 100,
            IsMoveToPointEnabled = true,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Value = app.Nivel,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };

        var valor = new TextBlock
        {
            Text = app.Mudo ? "Mudo" : $"{app.Nivel}%",
            FontSize = 13,
            FontWeight = FontWeights.Light,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        valor.SetResourceReference(TextBlock.FontFamilyProperty, "FonteNumero");
        valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        controle.ValueChanged += (_, e) =>
        {
            if (_ajustandoMixer) return;

            var nivel = (int)Math.Round(e.NewValue);
            MixerService.Definir(chave, nivel);

            // Subir o volume de um app mudo tira o mudo, como no Windows.
            if (e.NewValue > e.OldValue && _mudoNoMixer.TryGetValue(chave, out var mudo) && mudo)
            {
                MixerService.DefinirMudo(chave, false);
                _mudoNoMixer[chave] = false;
                quadro.Opacity = 1;
            }

            valor.Text = $"{nivel}%";
        };

        Grid.SetColumn(botao, 0);
        Grid.SetColumn(nome, 1);
        Grid.SetColumn(controle, 2);
        Grid.SetColumn(valor, 3);
        grade.Children.Add(botao);
        grade.Children.Add(nome);
        grade.Children.Add(controle);
        grade.Children.Add(valor);

        quadro.Opacity = app.Mudo ? 0.4 : 1;
        var linha = new LinhaDoMixer(chave, controle, valor, quadro, nome);
        grade.Tag = linha;
        _linhasDoMixer[chave] = linha;
        _mudoNoMixer[chave] = app.Mudo;
        return grade;
    }

    /// <summary>O icone do .exe, como o Windows mostra; para os sons do sistema, o alto-falante.</summary>
    private UIElement IconeDoApp(AppDeSom app)
    {
        if (!app.Sistema && app.Caminho is not null && AtalhoService.Icone(app.Caminho) is { } icone)
        {
            var imagem = new Image { Source = icone, Width = 18, Height = 18 };
            RenderOptions.SetBitmapScalingMode(imagem, BitmapScalingMode.HighQuality);
            return imagem;
        }

        var desenho = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(app.Sistema
                ? Corpo + OndaPerto
                : "M4,5 L20,5 L20,16 L4,16 Z M9,20 L15,20 M12,16 L12,20"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        };
        desenho.SetResourceReference(Shape.StrokeProperty, "Icone");
        return desenho;
    }

    /// <summary>A linha do mixer embaixo do cursor, para a roda do mouse.</summary>
    private LinhaDoMixer? LinhaSobOCursor()
    {
        if (MixerDeSom.Visibility != Visibility.Visible) return null;

        DependencyObject? atual = Mouse.DirectlyOver as DependencyObject;
        while (atual is not null)
        {
            if (atual is FrameworkElement { Tag: LinhaDoMixer linha }) return linha;
            if (ReferenceEquals(atual, MixerDeSom)) return null;
            atual = atual is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(atual)
                : LogicalTreeHelper.GetParent(atual);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Saidas de som

    /// <summary>
    /// Um botao por saida ativa, com a atual marcada como o perfil escolhido
    /// no painel. So aparece com mais de uma: com uma, nao ha o que trocar.
    /// </summary>
    private void MontarSaidas()
    {
        SaidasDeSom.Children.Clear();

        var saidas = _cfg.IlhaSaidasDeSom && _volume.Disponivel
            ? _volume.Saidas()
            : Array.Empty<SaidaDeSom>();

        SaidasDeSom.Visibility = Visivel(saidas.Count > 1);
        if (saidas.Count < 2) return;

        foreach (var saida in saidas)
        {
            var icone = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(DesenhoDaSaida(saida.Nome)),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            var nome = new TextBlock
            {
                Text = saida.NomeCurto,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(6, 0, 0, 0),
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var linha = new StackPanel { Orientation = Orientation.Horizontal };
            linha.Children.Add(icone);
            linha.Children.Add(nome);

            var fundo = new Border
            {
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(10, 5, 10, 5),
                BorderThickness = new Thickness(1),
                Child = linha
            };

            // As mesmas cores do perfil escolhido no painel.
            fundo.SetResourceReference(Border.BackgroundProperty, saida.Padrao ? "InfoFundo" : "Chip");
            fundo.SetResourceReference(Border.BorderBrushProperty, saida.Padrao ? "Acento" : "Trilho");
            icone.SetResourceReference(Shape.StrokeProperty, saida.Padrao ? "InfoTexto" : "TextoFraco");
            nome.SetResourceReference(TextBlock.ForegroundProperty, saida.Padrao ? "InfoTexto" : "Texto");

            var botao = new Button
            {
                Style = (Style)FindResource("BotaoSemMoldura"),
                Content = fundo,
                Tag = saida.Id,
                ToolTip = saida.Padrao ? $"{saida.Nome} (em uso)" : $"Tocar em {saida.Nome}",
                Margin = new Thickness(0, 0, 6, 6)
            };
            botao.Click += Saida_Click;
            SaidasDeSom.Children.Add(botao);
        }
    }

    private void Saida_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        _volume.UsarSaida(id);

        // O aparelho novo tem o volume dele.
        AplicarVolume(_volume.Nivel, _volume.Mudo, false);
        MontarSaidas();
    }

    /// <summary>Fone, monitor ou caixa, pelo nome que o Windows da ao aparelho.</summary>
    private static string DesenhoDaSaida(string nome)
    {
        var n = nome.ToLowerInvariant();

        if (new[] { "fone", "headphone", "headset", "airpods", "buds", "earphone", "auricular" }.Any(n.Contains))
            return "M4,15 L4,12 A8,8 0 0,1 20,12 L20,15 " +
                   "M4,15 A2,2 0 0,1 6,13 L7,13 A1,1 0 0,1 8,14 L8,19 A1,1 0 0,1 7,20 L6,20 A2,2 0 0,1 4,18 Z " +
                   "M20,15 A2,2 0 0,0 18,13 L17,13 A1,1 0 0,0 16,14 L16,19 A1,1 0 0,0 17,20 L18,20 A2,2 0 0,0 20,18 Z";

        if (new[] { "monitor", "hdmi", "displayport", "display", " tv", "nvidia", "amd high definition" }.Any(n.Contains))
            return "M4,5 L20,5 A1,1 0 0,1 21,6 L21,15 A1,1 0 0,1 20,16 L4,16 A1,1 0 0,1 3,15 L3,6 A1,1 0 0,1 4,5 Z " +
                   "M9,20 L15,20 M12,16 L12,20";

        return Corpo + OndaPerto;
    }

    // ------------------------------------------------------------------
    // Bandeja de arquivos

    /// <summary>Caminhos guardados, do mais antigo ao mais novo. So nesta sessao.</summary>
    private readonly List<string> _bandeja = new();

    /// <summary>Duas fileiras de seis.</summary>
    private const int MaximoNaBandeja = 12;

    private Point? _inicioDoArrasto;
    private DispatcherTimer? _fimDoRecado;

    /// <summary>
    /// Aceitar arquivos soltos em cima da Kisland.
    ///
    /// Pelo jeito antigo do Windows (WM_DROPFILES), e nao pelo arrastar do
    /// WPF, de proposito: o KInfo costuma rodar como administrador, e o
    /// Windows nao deixa o Explorer, que roda como usuario comum, arrastar
    /// para um app elevado pelo caminho novo (OLE). Pelo antigo deixa, desde
    /// que o app libere estas tres mensagens.
    ///
    /// O que faltava, e fazia a bandeja nunca receber nada: o WPF registra
    /// TODA janela que cria como destino OLE, tenha ou nao algum elemento
    /// com AllowDrop. Com esse registro o Windows nem tenta o caminho
    /// antigo — entrega ao WPF, que recusa (nada aqui aceita arquivos) ou,
    /// com o app elevado, nem isso. Cancelar o registro do WPF
    /// (RevokeDragDrop) deixa so o caminho antigo, que e o que funciona.
    /// </summary>
    private void PrepararBandeja()
    {
        try
        {
            var revogado = RevokeDragDrop(_handle);
            // 0 = cancelado; DRAGDROP_E_NOTREGISTERED = ja nao havia registro.
            if (revogado != 0 && revogado != NaoRegistrado)
                Log.Nota($"Bandeja: RevokeDragDrop devolveu 0x{revogado:X8}");

            var filtro = ChangeWindowMessageFilterEx(_handle, ArquivosSoltos, Permitir, IntPtr.Zero) &
                         ChangeWindowMessageFilterEx(_handle, CopiaDeDados, Permitir, IntPtr.Zero) &
                         ChangeWindowMessageFilterEx(_handle, CopiaDeDadosGlobal, Permitir, IntPtr.Zero);
            if (!filtro)
                Log.Nota($"Bandeja: ChangeWindowMessageFilterEx falhou ({Marshal.GetLastWin32Error()})");

            DragAcceptFiles(_handle, _cfg.IlhaBandeja);
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: preparar", ex);
        }
    }

    private void ReceberArquivos(IntPtr soltos)
    {
        var novos = new List<string>();

        try
        {
            var quantos = DragQueryFile(soltos, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < quantos; i++)
            {
                var tamanho = DragQueryFile(soltos, i, null, 0);
                var texto = new System.Text.StringBuilder((int)tamanho + 1);
                if (DragQueryFile(soltos, i, texto, (uint)texto.Capacity) > 0)
                    novos.Add(texto.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: receber", ex);
        }
        finally
        {
            DragFinish(soltos);
        }

        if (!_cfg.IlhaBandeja || novos.Count == 0) return;

        // Soltar de novo o mesmo arquivo so o traz para o fim; passar do
        // limite tira os mais antigos.
        foreach (var caminho in novos)
        {
            _bandeja.Remove(caminho);
            _bandeja.Add(caminho);
        }
        while (_bandeja.Count > MaximoNaBandeja) _bandeja.RemoveAt(0);

        MontarBandeja();
        AtualizarSemItens();
        MostrarEstado();

        // Abre para mostrar onde o arquivo foi parar.
        if (!_expandida && !_modoJogo) Expandir();
    }

    /// <summary>
    /// Refaz a bandeja. Arquivo apagado ou movido desde que entrou sai dela:
    /// a bandeja guarda so o caminho, e um caminho que nao leva a nada nao
    /// serve.
    /// </summary>
    private void MontarBandeja()
    {
        _bandeja.RemoveAll(c => !System.IO.File.Exists(c) && !System.IO.Directory.Exists(c));

        GradeBandeja.Children.Clear();
        foreach (var caminho in _bandeja)
            GradeBandeja.Children.Add(ItemDaBandeja(caminho));

        var quantos = _bandeja.Count;
        GradeBandeja.Visibility = Visivel(quantos > 0);
        AcoesBandeja.Visibility = Visivel(quantos > 0);
        DicaBandeja.Visibility = Visivel(quantos == 0);
        TituloDaBandeja();

        BlocoBandeja.Visibility = Visivel(_cfg.IlhaBandeja);
        AtualizarChipBandeja();

        // O que entrou ou saiu fica a vista dos outros aparelhos na hora.
        _kdrop?.BandejaMudou();
    }

    /// <summary>O chip da pilula: quantos arquivos, e o progresso do Kdrop quando algo esta chegando.</summary>
    private void AtualizarChipBandeja()
    {
        var quantos = _bandeja.Count;
        var recebendo = _recebendoKdrop is { Estado: EstadoDoPedido.Aceito };

        ChipBandeja.Visibility = Visivel(_cfg.IlhaBandeja && (quantos > 0 || recebendo) && !_modoJogo);
        TextoBandeja.Text = recebendo ? $"{quantos} · {_recebendoKdrop!.Porcentagem}%" : quantos.ToString();
    }

    private void TituloDaBandeja()
    {
        if (_recebendoKdrop is { Estado: EstadoDoPedido.Aceito } chegando)
        {
            TituloBandeja.Text = $"Bandeja · recebendo de {chegando.OrigemNome} · {chegando.Porcentagem}%";
            return;
        }

        var quantos = _bandeja.Count;
        TituloBandeja.Text = quantos switch
        {
            0 => "Bandeja",
            1 => "Bandeja · 1 arquivo",
            _ => $"Bandeja · {quantos} arquivos"
        };
    }

    private UIElement ItemDaBandeja(string caminho)
    {
        var pasta = System.IO.Directory.Exists(caminho);

        var circulo = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        circulo.SetResourceReference(Border.BackgroundProperty, "Chip");

        var icone = pasta ? null : AtalhoService.Icone(caminho);
        if (icone is not null)
        {
            var imagem = new Image { Source = icone, Width = 24, Height = 24 };
            RenderOptions.SetBitmapScalingMode(imagem, BitmapScalingMode.HighQuality);
            circulo.Child = imagem;
        }
        else
        {
            var desenho = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pasta
                    ? "M3,7 A2,2 0 0,1 5,5 L9,5 L11,7 L19,7 A2,2 0 0,1 21,9 L21,18 A2,2 0 0,1 19,20 L5,20 A2,2 0 0,1 3,18 Z"
                    : "M6,3 L14,3 L19,8 L19,19 A2,2 0 0,1 17,21 L7,21 A2,2 0 0,1 5,19 L5,4 A1,1 0 0,1 6,3 Z M14,3 L14,8 L19,8"),
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 18,
                Height = 18,
                Stretch = Stretch.Uniform
            };
            desenho.SetResourceReference(Shape.StrokeProperty, "Icone");
            circulo.Child = desenho;
        }

        var nome = System.IO.Path.GetFileName(caminho.TrimEnd('\\'));
        var rotulo = new TextBlock
        {
            Text = string.IsNullOrEmpty(nome) ? caminho : nome,
            FontSize = 10.5,
            Margin = new Thickness(0, 5, 0, 0),
            MaxWidth = 64,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        rotulo.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        var conteudo = new StackPanel();
        conteudo.Children.Add(circulo);
        conteudo.Children.Add(rotulo);

        var botao = new Button
        {
            Style = (Style)FindResource("BotaoAtalho"),
            Content = conteudo,
            Tag = caminho,
            ToolTip = caminho + "\nClique para abrir, ou arraste para levar a outro lugar"
        };
        botao.Click += ItemDaBandeja_Click;
        botao.PreviewMouseLeftButtonDown += (_, e) => _inicioDoArrasto = e.GetPosition(this);
        botao.PreviewMouseMove += ItemDaBandeja_Arrastar;

        var x = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 7,
            Height = 7,
            Stretch = Stretch.Uniform
        };
        x.SetResourceReference(Shape.StrokeProperty, "Icone");

        var tirar = new Button
        {
            Style = (Style)FindResource("BotaoRedondoPequeno"),
            Width = 18,
            Height = 18,
            Content = x,
            Tag = caminho,
            ToolTip = "Tirar da bandeja",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 4, 0)
        };
        tirar.Click += TirarDaBandeja_Click;

        var grade = new Grid();
        grade.Children.Add(botao);
        grade.Children.Add(tirar);
        return grade;
    }

    private void ItemDaBandeja_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string caminho }) return;

        Recolher();
        AtalhoService.Abrir(caminho);
    }

    /// <summary>
    /// Arrastar um item leva o arquivo para onde ele for solto: uma pasta, um
    /// e-mail, um chat. Movido para outro lugar, sai da bandeja.
    /// </summary>
    private void ItemDaBandeja_Arrastar(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _inicioDoArrasto is not { } inicio ||
            sender is not Button { Tag: string caminho } botao)
            return;

        var agora = e.GetPosition(this);
        if (Math.Abs(agora.X - inicio.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(agora.Y - inicio.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _inicioDoArrasto = null;

        // Com o Kdrop e algum aparelho por perto, o arrasto comeca dentro da
        // Kisland: soltar num aparelho manda o arquivo; sair da Kisland vira
        // o arrasto normal, para uma pasta ou outro programa.
        if (_kdrop is { Rodando: true } && _kdrop.Aparelhos().Count > 0)
        {
            ArrastarParaAparelho(botao, caminho);
            return;
        }

        ArrastarParaFora(botao, caminho);
    }

    private void ArrastarParaFora(Button botao, string caminho)
    {
        botao.ReleaseMouseCapture();

        try
        {
            var dados = new DataObject(DataFormats.FileDrop, new[] { caminho });
            DragDrop.DoDragDrop(botao, dados, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: arrastar", ex);
        }

        // Movido (ou apagado): o caminho nao existe mais, e MontarBandeja o tira.
        MontarBandeja();
        AtualizarSemItens();
    }

    // ------------------------------------------------------------------
    // Kdrop: a bandeja aberta para a casa

    private KdropService? _kdrop;

    /// <summary>Pedido esperando resposta; com ele, a Kisland mostra so a bandeja e nao fecha.</summary>
    private PedidoKdrop? _pedidoKdrop;

    /// <summary>Pedido aceito, com arquivos chegando: o progresso vai no titulo e no chip da bandeja.</summary>
    private PedidoKdrop? _recebendoKdrop;

    private bool _soBandeja;

    /// <summary>O que cada circulo diz embaixo do nome: "Mandando 42%", "Enviado".</summary>
    private readonly Dictionary<string, string> _estadoDoAparelho = new();

    private System.Windows.Media.Imaging.BitmapSource? _qr;
    private string? _qrDe;
    private string? _arrastandoParaAparelho;

    private const string DesenhoCelular =
        "M8,2 L16,2 A2,2 0 0,1 18,4 L18,20 A2,2 0 0,1 16,22 L8,22 A2,2 0 0,1 6,20 L6,4 A2,2 0 0,1 8,2 Z M11,18 L13,18";
    private const string DesenhoNotebook = "M5,6 L19,6 L19,15 L5,15 Z M3,18 L21,18";
    private const string DesenhoDesktop = "M4,5 L20,5 L20,16 L4,16 Z M9,20 L15,20 M12,16 L12,20";
    private const string DesenhoQr =
        "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z " +
        "M14,14 L16,14 M18,14 L20,14 L20,16 M14,18 L14,20 L16,20 M18,18 L20,18 L20,20";

    /// <summary>O Kdrop so existe com a Kisland ativa, a bandeja ligada e ele ligado.</summary>
    private bool KdropLigado => _ativa && _cfg.IlhaBandeja && _cfg.Kdrop;

    private void AtualizarKdrop()
    {
        if (KdropLigado && _kdrop is null)
        {
            _kdrop = new KdropService(_cfg, () =>
            {
                // O servidor pergunta de outra thread; a bandeja mora nesta.
                try { return Dispatcher.Invoke(() => _bandeja.ToList()); }
                catch { return new List<string>(); }
            });
            _kdrop.PedidoChegou += p => Dispatcher.BeginInvoke(() => MostrarPedido(p));
            _kdrop.PedidoMudou += p => Dispatcher.BeginInvoke(() => PedidoMudou(p));
            _kdrop.ArquivoRecebido += c => Dispatcher.BeginInvoke(() => ReceberDoKdrop(c));
            _kdrop.AparelhosMudaram += () => Dispatcher.BeginInvoke(() => MontarAparelhos());
            _kdrop.BandejasMudaram += () => Dispatcher.BeginInvoke(() => MontarBandejasDeFora());
            _kdrop.Iniciar();
        }
        else if (!KdropLigado && _kdrop is not null)
        {
            PararKdrop();
        }

        MontarAparelhos();
    }

    private void PararKdrop()
    {
        _kdrop?.Dispose();
        _kdrop = null;
        _pedidoKdrop = null;
        _recebendoKdrop = null;
        _soBandeja = false;
        _estadoDoAparelho.Clear();
        _buscando.Clear();
        BandejasDeFora.Children.Clear();
        BandejasDeFora.Visibility = Visibility.Collapsed;
        PedidoKdrop.Visibility = Visibility.Collapsed;
        QrKdrop.Visibility = Visibility.Collapsed;
        AreaKdrop.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Os aparelhos em circulos, como no AirDrop: PCs com KInfo e celulares
    /// com a bandeja aberta, e por ultimo o "Celular", que mostra o QR Code.
    /// </summary>
    private void MontarAparelhos()
    {
        AreaKdrop.Visibility = Visivel(_kdrop is not null);
        if (_kdrop is null) return;

        MotivoKdrop.Text = _kdrop.Motivo ?? "";
        MotivoKdrop.Visibility = Visivel(_kdrop.Motivo is not null);

        AparelhosKdrop.Children.Clear();
        if (!_kdrop.Rodando) return;

        var aparelhos = _kdrop.Aparelhos();
        foreach (var aparelho in aparelhos)
        {
            var desenho = !aparelho.EhPc ? DesenhoCelular : aparelho.EhNotebook ? DesenhoNotebook : DesenhoDesktop;
            _estadoDoAparelho.TryGetValue(aparelho.Id, out var estado);
            AparelhosKdrop.Children.Add(CirculoDeAparelho(aparelho, aparelho.Nome, desenho, estado ?? "Pronto"));
        }

        AparelhosKdrop.Children.Add(CirculoDeAparelho("qr", "Celular", DesenhoQr, "QR Code"));

        DicaKdrop.Text = aparelhos.Count == 0
            ? "· nenhum aparelho por perto ainda"
            : "· toque para mandar tudo, ou arraste um arquivo até o aparelho";

        MontarBandejasDeFora();
    }

    /// <summary>Itens sendo trazidos de outro PC: "42%", ou "Não veio" quando falha.</summary>
    private readonly Dictionary<string, string> _buscando = new();

    /// <summary>
    /// As bandejas dos outros PCs da casa, cada uma com o nome do PC em cima.
    /// Toque num item traz para este PC: vai para a pasta do Kdrop e entra
    /// nesta bandeja.
    /// </summary>
    private void MontarBandejasDeFora()
    {
        BandejasDeFora.Children.Clear();

        var bandejas = _kdrop is { Rodando: true } kdrop ? kdrop.BandejasDosPcs() : Array.Empty<(AparelhoKdrop, IReadOnlyList<ItemKdrop>)>();
        BandejasDeFora.Visibility = Visivel(bandejas.Count > 0);

        foreach (var (pc, itens) in bandejas)
        {
            var titulo = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 10, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titulo.Inlines.Add(new System.Windows.Documents.Run($"Na bandeja de {pc.Nome}") { FontWeight = FontWeights.Medium });
            titulo.Inlines.Add(new System.Windows.Documents.Run(" · toque para trazer"));
            titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            BandejasDeFora.Children.Add(titulo);

            var grade = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var item in itens.Take(MaximoNaBandeja))
                grade.Children.Add(ItemDeFora(pc, item));
            BandejasDeFora.Children.Add(grade);
        }
    }

    private Button ItemDeFora(AparelhoKdrop pc, ItemKdrop item)
    {
        var chave = pc.Id + "/" + item.Id;
        _buscando.TryGetValue(chave, out var estado);

        var circulo = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        circulo.SetResourceReference(Border.BackgroundProperty, "Chip");
        if (estado is not null && estado.EndsWith('%'))
        {
            circulo.BorderThickness = new Thickness(2);
            circulo.SetResourceReference(Border.BorderBrushProperty, "Acento");
        }

        var desenho = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(item.Pasta
                ? "M3,7 A2,2 0 0,1 5,5 L9,5 L11,7 L19,7 A2,2 0 0,1 21,9 L21,18 A2,2 0 0,1 19,20 L5,20 A2,2 0 0,1 3,18 Z"
                : "M6,3 L14,3 L19,8 L19,19 A2,2 0 0,1 17,21 L7,21 A2,2 0 0,1 5,19 L5,4 A1,1 0 0,1 6,3 Z M14,3 L14,8 L19,8"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform
        };
        desenho.SetResourceReference(Shape.StrokeProperty, "Icone");
        circulo.Child = desenho;

        var rotulo = new TextBlock
        {
            Text = estado ?? item.Nome,
            FontSize = 10.5,
            Margin = new Thickness(0, 5, 0, 0),
            MaxWidth = 64,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        rotulo.SetResourceReference(TextBlock.ForegroundProperty, estado is null ? "TextoFraco" : "InfoTexto");

        var conteudo = new StackPanel();
        conteudo.Children.Add(circulo);
        conteudo.Children.Add(rotulo);

        var detalhe = item.Pasta ? "pasta, chega aberta" : Tamanho(item.Tamanho);
        var botao = new Button
        {
            Style = (Style)FindResource("BotaoAtalho"),
            Content = conteudo,
            ToolTip = $"{item.Nome} · {detalhe}\nNa bandeja de {pc.Nome}. Clique para trazer para este PC"
        };
        botao.Click += async (_, _) => await BuscarDeFora(pc, item);
        return botao;
    }

    private async Task BuscarDeFora(AparelhoKdrop pc, ItemKdrop item)
    {
        if (_kdrop is null) return;

        var chave = pc.Id + "/" + item.Id;
        if (_buscando.TryGetValue(chave, out var atual) && atual.EndsWith('%')) return;

        void Mostrar(string? texto)
        {
            if (texto is null) _buscando.Remove(chave);
            else _buscando[chave] = texto;
            MontarBandejasDeFora();
        }

        Mostrar("0%");
        var caminho = await _kdrop.BuscarDePc(pc, item, new Progress<int>(p => Mostrar($"{p}%")));

        if (caminho is null)
        {
            Mostrar("Não veio");
            return;
        }

        Mostrar(null);
        ReceberDoKdrop(caminho);
    }

    private Button CirculoDeAparelho(object alvo, string nome, string desenho, string estado)
    {
        var ehQr = alvo is "qr";

        var circulo = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(26),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (ehQr)
        {
            circulo.BorderThickness = new Thickness(1.5);
            circulo.SetResourceReference(Border.BorderBrushProperty,
                QrKdrop.Visibility == Visibility.Visible ? "Acento" : "Trilho");
            circulo.Background = Brushes.Transparent;
        }
        else
        {
            circulo.SetResourceReference(Border.BackgroundProperty, "Chip");
            // Mandando: anel na cor de destaque; enviado: anel verde.
            if (estado.StartsWith("Mandando") || estado.StartsWith("Esperando"))
            {
                circulo.BorderThickness = new Thickness(2);
                circulo.SetResourceReference(Border.BorderBrushProperty, "Acento");
            }
            else if (estado is "Enviado" or "Mandado")
            {
                circulo.BorderThickness = new Thickness(2);
                circulo.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            }
        }

        var icone = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(desenho),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform
        };
        icone.SetResourceReference(Shape.StrokeProperty, "Icone");

        var dentro = new Grid();
        dentro.Children.Add(icone);
        if (!ehQr)
        {
            // Ponto verde de "ligado", no canto do circulo.
            dentro.Children.Add(new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                StrokeThickness = 2,
                Stroke = (Brush)FindResource("Cartao"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -1, -1)
            });
        }
        circulo.Child = dentro;

        var rotulo = new TextBlock
        {
            Text = nome,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 6, 0, 0),
            MaxWidth = 84,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        rotulo.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        var situacao = new TextBlock
        {
            Text = estado,
            FontSize = 10,
            Margin = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        situacao.SetResourceReference(TextBlock.ForegroundProperty,
            estado is "Pronto" or "QR Code" ? "TextoFraco" : "InfoTexto");

        var conteudo = new StackPanel { Width = 84, Margin = new Thickness(0, 0, 4, 0) };
        conteudo.Children.Add(circulo);
        conteudo.Children.Add(rotulo);
        conteudo.Children.Add(situacao);

        var botao = new Button
        {
            Style = (Style)FindResource("BotaoSemMoldura"),
            Content = conteudo,
            Tag = alvo,
            ToolTip = ehQr ? "Abrir a bandeja no celular" : $"Mandar a bandeja para {nome}"
        };
        botao.Click += Aparelho_Click;
        return botao;
    }

    private async void Aparelho_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button botao) return;

        if (botao.Tag is "qr")
        {
            AlternarQr();
            return;
        }

        if (botao.Tag is AparelhoKdrop aparelho)
            await MandarPeloKdrop(aparelho, _bandeja.ToList());
    }

    /// <summary>
    /// Manda arquivos da bandeja para um aparelho. Para um PC, pede e envia;
    /// para um celular, marca os arquivos para ele — a pagina aberta la os
    /// destaca, prontos para baixar. Pastas ficam de fora: so arquivos viajam.
    /// </summary>
    private async Task MandarPeloKdrop(AparelhoKdrop aparelho, List<string> caminhos)
    {
        if (_kdrop is null) return;

        var arquivos = caminhos.Where(System.IO.File.Exists).ToList();
        if (arquivos.Count == 0)
        {
            Situacao(aparelho.Id, "Bandeja vazia");
            return;
        }

        if (!aparelho.EhPc)
        {
            _kdrop.AvisarCelular(aparelho.Id, arquivos);
            Situacao(aparelho.Id, "Enviado");
            return;
        }

        if (_estadoDoAparelho.TryGetValue(aparelho.Id, out var atual) &&
            (atual.StartsWith("Mandando") || atual.StartsWith("Esperando")))
            return;

        Situacao(aparelho.Id, "Esperando…");
        var progresso = new Progress<int>(p => Situacao(aparelho.Id, $"Mandando {p}%"));
        var resultado = await _kdrop.MandarParaPc(aparelho, arquivos, progresso);
        Situacao(aparelho.Id, resultado);
    }

    private void Situacao(string id, string texto)
    {
        _estadoDoAparelho[id] = texto;
        MontarAparelhos();
    }

    private void AlternarQr()
    {
        if (QrKdrop.Visibility == Visibility.Visible)
        {
            QrKdrop.Visibility = Visibility.Collapsed;
            MontarAparelhos();
            return;
        }

        var endereco = KdropService.Endereco;
        if (endereco is null)
        {
            MotivoKdrop.Text = "Sem rede agora: verifique a conexão com a internet.";
            MotivoKdrop.Visibility = Visibility.Visible;
            return;
        }

        if (_qrDe != endereco)
        {
            _qr = GerarQr(endereco);
            _qrDe = endereco;
        }

        ImagemQrKdrop.Source = _qr;
        EnderecoKdrop.Text = endereco.Replace("http://", "").TrimEnd('/');
        QrKdrop.Visibility = Visibility.Visible;
        MontarAparelhos();
    }

    private static System.Windows.Media.Imaging.BitmapSource? GerarQr(string texto)
    {
        try
        {
            using var gerador = new QRCoder.QRCodeGenerator();
            using var dados = gerador.CreateQrCode(texto, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(dados).GetGraphic(8);

            var imagem = new System.Windows.Media.Imaging.BitmapImage();
            imagem.BeginInit();
            imagem.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            imagem.StreamSource = new System.IO.MemoryStream(png);
            imagem.EndInit();
            imagem.Freeze();
            return imagem;
        }
        catch (Exception ex)
        {
            Log.Erro("Kdrop: QR Code", ex);
            return null;
        }
    }

    /// <summary>
    /// Chegou um pedido: a Kisland abre so na bandeja, com Aceitar e Recusar,
    /// e nao fecha ate a resposta (ou o prazo). Escondida por tela cheia ou
    /// com o PC parado, o pedido espera; quem manda ve "esperando".
    /// </summary>
    private void MostrarPedido(PedidoKdrop pedido)
    {
        _pedidoKdrop = pedido;

        var n = pedido.Arquivos.Count;
        TituloPedidoKdrop.Text = $"{pedido.OrigemNome} quer colocar {(n == 1 ? "1 arquivo" : $"{n} arquivos")} na sua bandeja";

        var nomes = string.Join(", ", pedido.Arquivos.Take(3).Select(a => a.Nome));
        if (n > 3) nomes += $" e mais {n - 3}";
        DetalhePedidoKdrop.Text = $"{nomes} · {Tamanho(pedido.Total)}";

        IconePedidoKdrop.Data = Geometry.Parse(pedido.OrigemEhPc ? DesenhoDesktop : DesenhoCelular);
        LembrarPedidoKdrop.IsChecked = false;
        PedidoKdrop.Visibility = Visibility.Visible;
        _soBandeja = true;

        if (!_ativa || _escondidaPorTelaCheia) return;

        // Escondida pelo PC parado: o pedido a traz de volta. Quem esta
        // mandando esta no celular, e o PC ficou parado justamente por isso.
        if (_escondidaPorInatividade)
        {
            _escondidaPorInatividade = false;
            Show();
            Posicionar();
        }

        if (_expandida) AplicarMidia(false);
        else Expandir();
    }

    private void AceitarKdrop_Click(object sender, RoutedEventArgs e) => ResponderPedido(true);
    private void RecusarKdrop_Click(object sender, RoutedEventArgs e) => ResponderPedido(false);

    private void ResponderPedido(bool aceitar)
    {
        if (_pedidoKdrop is not { } pedido) return;

        _kdrop?.Responder(pedido.Id, aceitar, aceitar && LembrarPedidoKdrop.IsChecked == true);
        FecharPedido();
    }

    private void FecharPedido()
    {
        _pedidoKdrop = null;
        _soBandeja = false;
        PedidoKdrop.Visibility = Visibility.Collapsed;
        AplicarConfiguracao();
    }

    /// <summary>Prazo do pedido vencido sem resposta: conta como recusado.</summary>
    private void ConferirPedidoKdrop()
    {
        if (_pedidoKdrop is { } pedido && DateTime.UtcNow - pedido.CriadoEm > KdropService.PrazoDoPedido)
            ResponderPedido(false);
    }

    private void PedidoMudou(PedidoKdrop pedido)
    {
        // Respondido por outro caminho (prazo no servidor, aparelho confiavel).
        if (_pedidoKdrop?.Id == pedido.Id && pedido.Estado != EstadoDoPedido.Esperando)
            FecharPedido();

        if (pedido.Estado == EstadoDoPedido.Aceito) _recebendoKdrop = pedido;
        else if (_recebendoKdrop?.Id == pedido.Id) _recebendoKdrop = null;

        TituloDaBandeja();
        AtualizarChipBandeja();
        AtualizarSemItens();
        MostrarEstado();
    }

    /// <summary>Um arquivo chegou inteiro: entra na bandeja, como se tivesse sido arrastado.</summary>
    private void ReceberDoKdrop(string caminho)
    {
        _bandeja.Remove(caminho);
        _bandeja.Add(caminho);
        while (_bandeja.Count > MaximoNaBandeja) _bandeja.RemoveAt(0);

        MontarBandeja();
        AtualizarSemItens();
        MostrarEstado();
    }

    private static string Tamanho(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
    };

    // Arrastar um arquivo da bandeja ate um aparelho, sem sair da Kisland.
    // O arrasto do Windows (OLE) nao serve aqui: a Kisland cancelou o
    // registro dela como destino, para a bandeja receber do Explorer. Entao o
    // arrasto interno e feito a mao, com o mouse preso ao item.

    private void ArrastarParaAparelho(Button botao, string caminho)
    {
        _arrastandoParaAparelho = caminho;
        botao.CaptureMouse();
        botao.PreviewMouseMove += ArrastoInterno_Mover;
        botao.PreviewMouseLeftButtonUp += ArrastoInterno_Soltar;
        botao.LostMouseCapture += ArrastoInterno_Perdido;
        Mouse.OverrideCursor = Cursors.Hand;
    }

    private void ArrastoInterno_Mover(object sender, MouseEventArgs e)
    {
        if (_arrastandoParaAparelho is not { } caminho || sender is not Button botao) return;

        // Saiu da Kisland: vira o arrasto normal, para uma pasta ou programa.
        if (!CursorEmCima())
        {
            EncerrarArrastoInterno(botao);
            ArrastarParaFora(botao, caminho);
        }
    }

    private void ArrastoInterno_Soltar(object sender, MouseButtonEventArgs e)
    {
        if (_arrastandoParaAparelho is not { } caminho || sender is not Button botao) return;

        var alvo = AparelhoSob(e.GetPosition(Casca));
        EncerrarArrastoInterno(botao);

        // Sem isso o soltar viraria clique, e o arquivo abriria.
        e.Handled = true;

        if (alvo is not null) _ = MandarPeloKdrop(alvo, new List<string> { caminho });
    }

    private void ArrastoInterno_Perdido(object sender, MouseEventArgs e)
    {
        if (sender is Button botao) EncerrarArrastoInterno(botao);
    }

    private void EncerrarArrastoInterno(Button botao)
    {
        if (_arrastandoParaAparelho is null) return;
        _arrastandoParaAparelho = null;

        botao.PreviewMouseMove -= ArrastoInterno_Mover;
        botao.PreviewMouseLeftButtonUp -= ArrastoInterno_Soltar;
        botao.LostMouseCapture -= ArrastoInterno_Perdido;
        Mouse.OverrideCursor = null;
        if (botao.IsMouseCaptured) botao.ReleaseMouseCapture();
    }

    /// <summary>O aparelho (circulo) que esta embaixo de um ponto da Kisland, se houver.</summary>
    private AparelhoKdrop? AparelhoSob(Point ponto)
    {
        var no = Casca.InputHitTest(ponto) as DependencyObject;
        while (no is not null)
        {
            if (no is Button { Tag: AparelhoKdrop aparelho }) return aparelho;
            no = no is Visual ? VisualTreeHelper.GetParent(no) : null;
        }

        return null;
    }

    private void TirarDaBandeja_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string caminho }) return;

        _bandeja.Remove(caminho);
        MontarBandeja();
        AtualizarSemItens();
    }

    /// <summary>
    /// Copia todos para a area de transferencia, como um Ctrl+C no Explorer:
    /// colar numa pasta copia os arquivos para la. Funciona tambem com o app
    /// elevado, onde arrastar para fora pode nao funcionar.
    /// </summary>
    private void CopiarBandeja_Click(object sender, RoutedEventArgs e)
    {
        if (_bandeja.Count == 0) return;

        try
        {
            var lista = new System.Collections.Specialized.StringCollection();
            lista.AddRange(_bandeja.ToArray());
            Clipboard.SetFileDropList(lista);

            TituloBandeja.Text = "Copiado! Cole com Ctrl+V";
            _fimDoRecado?.Stop();
            _fimDoRecado = Temporizador(TimeSpan.FromSeconds(2.5), () =>
            {
                _fimDoRecado?.Stop();
                TituloDaBandeja();
            });
            _fimDoRecado.Start();
        }
        catch (Exception ex)
        {
            // A area de transferencia pode estar presa por outro app.
            Log.Erro("Bandeja: copiar", ex);
        }
    }

    private void LimparBandeja_Click(object sender, RoutedEventArgs e)
    {
        _bandeja.Clear();
        MontarBandeja();
        AtualizarSemItens();
    }

    // ------------------------------------------------------------------
    // Perfis e atalhos para o painel

    /// <summary>O plano pode ter mudado fora do app; vale o que esta em vigor.</summary>
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

        // Desempenho nao e so um plano: e a tela que pergunta o que fechar e
        // limpar. Essa tela mora no painel.
        if (ReferenceEquals(sender, PerfilDesempenho))
        {
            Recolher();
            PedirDesempenho?.Invoke();
            SincronizarPerfil();
            return;
        }

        var perfil = ReferenceEquals(sender, PerfilEconomico)
            ? PerfilEnergia.Economico
            : PerfilEnergia.Equilibrado;

        if (!PowerService.Aplicar(perfil)) SincronizarPerfil();
    }

    private void AbrirPainel_Click(object sender, RoutedEventArgs e)
    {
        Recolher();
        PedirPainel?.Invoke();
    }

    private void Personalizar_Click(object sender, RoutedEventArgs e)
    {
        Recolher();
        PedirPersonalizacao?.Invoke();
    }

    // ------------------------------------------------------------------

    private const int RodaDoMouse = 0x020A;
    private const int BotaoDoMeioSolto = 0x0208;
    private const int ArquivosSoltos = 0x0233;
    private const uint CopiaDeDados = 0x004A;
    private const uint CopiaDeDadosGlobal = 0x0049;
    private const uint Permitir = 1;

    private const int EstiloEstendido = -20;
    private const int SemAtivar = 0x08000000;
    private const int JanelaDeFerramenta = 0x00000080;
    private const int TeclaBotaoEsquerdo = 0x01;
    private const uint SemTamanho = 0x0001;
    private const uint SemOrdem = 0x0004;
    private const uint SemAtivarJanela = 0x0010;
    private const uint SemMover = 0x0002;
    private static readonly IntPtr PorCimaDeTudo = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Ponto
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Retangulo
    {
        public int Esquerda;
        public int Topo;
        public int Direita;
        public int Baixo;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr janela, int indice);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr janela, int indice, int valor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Ponto ponto);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr janela, uint mensagem, uint acao, IntPtr extra);

    /// <summary>DRAGDROP_E_NOTREGISTERED: a janela nao tinha destino OLE.</summary>
    private const int NaoRegistrado = unchecked((int)0x80040100);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr janela);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr janela, [MarshalAs(UnmanagedType.Bool)] bool aceitar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr soltos, uint indice, System.Text.StringBuilder? arquivo, uint tamanho);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr soltos);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr janela, out int processo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int tecla);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr janela, out Retangulo retangulo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr janela, IntPtr depoisDe, int x, int y,
                                            int largura, int altura, uint sinalizadores);
}
