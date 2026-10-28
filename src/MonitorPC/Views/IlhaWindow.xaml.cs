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
    /// <summary>Tempo com o cursor em cima antes de abrir: passar de raspao nao abre.</summary>
    private static readonly TimeSpan EsperaParaAbrir = TimeSpan.FromMilliseconds(180);

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

    private DateTime? _dentroDesde;
    private DateTime? _foraDesde;

    // Limpeza do player depois de um tempo pausado.
    private DateTime? _pausadoDesde;
    private string? _faixaDispensada;

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

        SourceInitialized += (_, _) => NaoRoubarFoco();
        SizeChanged += (_, _) => Posicionar();
        DpiChanged += (_, _) => Posicionar();

        // Monitor ligado, desligado ou com resolucao nova: a ilha volta ao
        // topo do monitor dela, ou do principal se o dela sumiu.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += Telas_Mudaram;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= Telas_Mudaram;

        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() => AplicarSensores(snap));
        _midia.Mudou += _ => Dispatcher.BeginInvoke(() => AplicarMidia(true));
        _volume.Mudou += (nivel, mudo) => Dispatcher.BeginInvoke(() => AplicarVolume(nivel, mudo, true));

        AplicarConfiguracao();
    }

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
        Hide();
    }

    /// <summary>
    /// Aplica o que a Personalizacao decide: fundo, transparencia e o que a
    /// pilula mostra. Chamada a cada mudanca la, para o efeito ser visto na hora.
    /// </summary>
    public void AplicarConfiguracao()
    {
        // Mesma regra do painel: a transparencia vai no alpha do fundo, e nao na
        // opacidade da janela, que desbotaria texto e icones junto.
        var fundo = ThemeService.CorDeFundo;
        if (_cfg.Transparencia)
        {
            var alpha = (byte)(Math.Clamp(_cfg.Opacidade, AppConfig.OpacidadeMinima, 100) * 255 / 100);
            Casca.Background = new SolidColorBrush(Color.FromArgb(alpha, fundo.R, fundo.G, fundo.B));
        }
        else
        {
            Casca.Background = new SolidColorBrush(fundo);
        }

        ChipCpu.Visibility = Visivel(_cfg.IlhaCpu || _cfg.IlhaCpuUso);
        ChipGpu.Visibility = Visivel(_cfg.IlhaGpu || _cfg.IlhaGpuUso);
        ChipRam.Visibility = Visivel(_cfg.IlhaRam);
        ChipDisco.Visibility = Visivel(_cfg.IlhaDisco);
        ChipPotencia.Visibility = Visivel(_cfg.IlhaPotencia);
        ChipVolume.Visibility = Visivel(_cfg.IlhaVolume && _volume.Disponivel);
        ChipRelogio.Visibility = Visivel(_cfg.IlhaRelogio);
        BlocoVolume.Visibility = Visivel(_volume.Disponivel);

        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AtualizarRelogio();
        AplicarMidia(false);
    }

    private static Visibility Visivel(bool sim) => sim ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Sem nada para mostrar, a pilula vira um circulo com o anel. O botao de
    /// Personalizacao sai junto: ao lado do anel ele seria o unico conteudo, e
    /// a ilha aberta continua com ele.
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
                    ChipRelogio.Visibility == Visibility.Visible;

        SemItens.Visibility = Visivel(!algum);
        BotaoPersonalizarPilula.Visibility = Visivel(algum);
        Compacta.Margin = algum ? new Thickness(6, 0, 7, 0) : new Thickness(6, 0, 6, 0);
    }

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

            SetWindowPos(_handle, IntPtr.Zero, x, area.Top, 0, 0,
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

        if (_exigirSaida)
        {
            if (dentro) return;
            _exigirSaida = false;
        }

        if (dentro)
        {
            _foraDesde = null;

            if (!_expandida && _cfg.IlhaAbrirAoPassarMouse && !BotaoEsquerdoApertado())
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

        _foraDesde ??= agora;
        if (agora - _foraDesde >= EsperaParaFechar) Recolher();
    }

    private bool CursorEmCima()
    {
        if (!GetCursorPos(out var cursor) || Casca.ActualWidth <= 0) return false;

        try
        {
            var inicio = Casca.PointToScreen(new Point(0, 0));
            var fim = Casca.PointToScreen(new Point(Casca.ActualWidth, Casca.ActualHeight));

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

    private void Expandir()
    {
        if (_expandida || !_ativa) return;

        _expandida = true;
        _dentroDesde = null;
        _foraDesde = null;
        _aviso = Aviso.Nenhum;
        _fimDoAviso.Stop();

        SincronizarPerfil();
        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AplicarMidia(false);
        AplicarVolume(_volume.Nivel, _volume.Mudo, false);
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

        ConferirTelaCheia();
        ConferirSilencio();
        AtualizarRelogio();

        if (_expandida) AtualizarProgresso();
    }

    private void AtualizarRelogio()
    {
        if (_cfg.IlhaRelogio) TextoRelogio.Text = DateTime.Now.ToString("HH:mm");
    }

    private void ConferirTelaCheia()
    {
        var cheia = _cfg.IlhaEsconderEmTelaCheia && TelaCheia.Agora(_handle);

        if (cheia && IsVisible)
        {
            _escondidaPorTelaCheia = true;
            _expandida = false;
            _aviso = Aviso.Nenhum;
            MostrarEstado();
            Hide();
        }
        else if (!cheia && _escondidaPorTelaCheia)
        {
            _escondidaPorTelaCheia = false;
            Show();
            Posicionar();
            MostrarEstado();
        }
    }

    /// <summary>
    /// Pausado por tres minutos, o player sai da ilha: musica parada ha tanto
    /// tempo deixou de ser "tocando agora". Volta sozinho quando algo toca, ou
    /// quando o player anuncia outra faixa.
    /// </summary>
    private void ConferirSilencio()
    {
        if (_pausadoDesde is not { } desde || _faixaDispensada is not null) return;
        if (DateTime.UtcNow - desde < SilencioQueLimpa) return;
        if (_midia.Atual is not { } estado) return;

        _faixaDispensada = Chave(estado);
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
        var pilulaNaTela = _ativa && !_escondidaPorTelaCheia && Compacta.Visibility == Visibility.Visible;

        if (pilulaNaTela && MidiaVisivel() is { Tocando: true } && _cfg.IlhaMusica) AndarEqualizador();
        else PararEqualizador();

        if (pilulaNaTela && SemItens.Visibility == Visibility.Visible) AndarAnel();
        else PararAnel();
    }

    private void MostrarAviso(Aviso aviso, TimeSpan duracao)
    {
        if (!_ativa || _expandida || _escondidaPorTelaCheia) return;

        _aviso = aviso;
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
        TextoCpu.Text = Rotulo("CPU", _cfg.IlhaCpu ? s.CpuTemp : null, _cfg.IlhaCpuUso ? s.CpuUso : null);
        TextoGpu.Text = Rotulo("GPU", _cfg.IlhaGpu ? s.GpuTemp : null, _cfg.IlhaGpuUso ? s.GpuUso : null);
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

        Rodape.Text = s.SensoresDeBaixoNivel
            ? "Sensores ativos"
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

    private static string Chave(EstadoDeMidia e) => e.Titulo + "\u0001" + e.Artista;

    /// <summary>O que a ilha mostra: o que toca, menos o que ja foi dispensado por silencio.</summary>
    private EstadoDeMidia? MidiaVisivel()
    {
        var estado = _midia.Atual;
        if (estado is null) return null;

        return _faixaDispensada is not null && !estado.Tocando && Chave(estado) == _faixaDispensada
            ? null
            : estado;
    }

    private void AplicarMidia(bool veioDeAviso)
    {
        var bruto = _midia.Atual;

        // Conta do silencio: comeca ao pausar, zera ao tocar. Tocar de novo, ou
        // outra faixa, traz o player de volta.
        if (bruto is null)
        {
            _pausadoDesde = null;
            _faixaDispensada = null;
        }
        else if (bruto.Tocando)
        {
            _pausadoDesde = null;
            _faixaDispensada = null;
        }
        else
        {
            _pausadoDesde ??= DateTime.UtcNow;
            if (_faixaDispensada is not null && _faixaDispensada != Chave(bruto))
            {
                _faixaDispensada = null;
                _pausadoDesde = DateTime.UtcNow;
            }
        }

        var estado = MidiaVisivel();

        var mostrar = estado is not null && _cfg.IlhaMusica;
        MidiaCompacta.Visibility = Visivel(mostrar);

        PausadoPequeno.Visibility = Visivel(estado is { Tocando: false });
        Equalizador.Visibility = Visivel(estado is not { Tocando: false });

        PorCapa(CapaPequena, NotaPequena, estado?.Capa);
        PorCapa(CapaGrande, NotaGrande, estado?.Capa);

        BlocoMidia.Visibility = Visivel(estado is not null);
        BlocoProgresso.Visibility = Visivel(estado is { Duracao.TotalSeconds: > 0 });
        SemMidia.Visibility = Visivel(estado is null);

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

    // Alto-falante de cantos arredondados, com ondas que acompanham o nivel.
    // O desenho anterior era de retas e quinas, e destoava do resto da ilha.
    private const string Corpo =
        "M3,10.5 C3,9.7 3.7,9 4.5,9 L7,9 L10.8,5.6 C11.5,5 12.5,5.5 12.5,6.4 " +
        "L12.5,17.6 C12.5,18.5 11.5,19 10.8,18.4 L7,15 L4.5,15 C3.7,15 3,14.3 3,13.5 Z";
    private const string OndaPerto = " M16,9.5 C17.3,10.8 17.3,13.2 16,14.5";
    private const string OndaLonge = " M18.8,7 C21.5,9.7 21.5,14.3 18.8,17";
    private const string Corte = " M16.5,10 L20.5,14 M20.5,10 L16.5,14";

    private static Geometry IconeDoVolume(int nivel, bool mudo)
    {
        var desenho = mudo || nivel == 0 ? Corpo + Corte
                    : nivel < 50 ? Corpo + OndaPerto
                    : Corpo + OndaPerto + OndaLonge;

        return Geometry.Parse(desenho);
    }

    private void AplicarVolume(int nivel, bool mudo, bool veioDeAviso)
    {
        var icone = IconeDoVolume(nivel, mudo);
        IconeVolumeChip.Data = icone;
        IconeAvisoVolume.Data = icone;
        IconeMudo.Data = icone;

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

    private const int EstiloEstendido = -20;
    private const int SemAtivar = 0x08000000;
    private const int JanelaDeFerramenta = 0x00000080;
    private const int TeclaBotaoEsquerdo = 0x01;
    private const uint SemTamanho = 0x0001;
    private const uint SemOrdem = 0x0004;
    private const uint SemAtivarJanela = 0x0010;

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
