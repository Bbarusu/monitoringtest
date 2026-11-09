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

public partial class IlhaWindow : Window
{
    private TimeSpan EsperaParaAbrir => TimeSpan.FromMilliseconds(Math.Clamp(_cfg.IlhaEsperaParaAbrirMs, 150, 10000));

    private static readonly TimeSpan EsperaParaFechar = TimeSpan.FromMilliseconds(450);

    private static readonly TimeSpan SilencioQueLimpa = TimeSpan.FromMinutes(1);

    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly AlertService _alertas;
    private readonly MidiaService _midia;
    private readonly VolumeService _volume;

    private readonly DispatcherTimer _relogio;
    private readonly DispatcherTimer _cursor;
    private readonly DispatcherTimer _fimDoAviso;

    private enum Aviso { Nenhum, Volume, Faixa, Texto }

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

    private readonly FpsService _fps = new();
    private Jogo? _jogo;
    private bool _modoJogo;
    private int _tentarFpsEm;

    private double _fpsMostrado;

    private DateTime _diaDoCalendario;

    private DateTime? _dentroDesde;
    private DateTime? _foraDesde;

    private DateTime _somDaMidiaEm = DateTime.UtcNow;
    private string? _faixaDispensada;
    private bool _tocandoAoDispensar;

    public bool Ativa => _ativa;

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

            HwndSource.FromHwnd(_handle)?.AddHook(Mensagem);
            PrepararBandeja();
        };
        SizeChanged += (_, _) => Posicionar();
        DpiChanged += (_, _) => Posicionar();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += Telas_Mudaram;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= Telas_Mudaram;

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
        TrazerParaFrente();
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

    public void AplicarConfiguracao()
    {
        AplicarPreta();

        var fundo = _cfg.IlhaPreta ? Colors.Black : ThemeService.CorDeFundo;
        if (TransparenciaDaIlha)
        {
            var opacidade = _cfg.IlhaOpacidade ?? _cfg.Opacidade;
            var alpha = (byte)(Math.Clamp(opacidade, AppConfig.OpacidadeMinima, 100) * 255 / 100);
            Casca.Background = new SolidColorBrush(Color.FromArgb(alpha, fundo.R, fundo.G, fundo.B));
        }
        else
        {
            Casca.Background = new SolidColorBrush(fundo);
        }

        var escala = Math.Clamp(_cfg.IlhaEscala, 70, 100) / 100.0;
        Casca.LayoutTransform = new ScaleTransform(escala, escala);

        AplicarContorno();

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

    private bool MostraCpuTemp => _modoJogo ? _cfg.IlhaJogoCpu : _cfg.IlhaCpu;
    private bool MostraCpuUso => _modoJogo ? _cfg.IlhaJogoCpuUso : _cfg.IlhaCpuUso;
    private bool MostraGpuTemp => _modoJogo ? _cfg.IlhaJogoGpu : _cfg.IlhaGpu;
    private bool MostraGpuUso => _modoJogo ? _cfg.IlhaJogoGpuUso : _cfg.IlhaGpuUso;
    private bool MostraRam => _modoJogo ? _cfg.IlhaJogoRam : _cfg.IlhaRam;
    private bool MostraPotencia => _modoJogo ? _cfg.IlhaJogoPotencia : _cfg.IlhaPotencia;
    private bool MostraRelogio => _modoJogo ? _cfg.IlhaJogoRelogio : _cfg.IlhaRelogio;
    private bool MostraMusica => _modoJogo ? _cfg.IlhaJogoMusica : _cfg.IlhaMusica;

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

    private bool TransparenciaDaIlha => _cfg.TransparenciaAtiva && !_cfg.IlhaPreta;

    private System.Windows.Media.Effects.Effect? _sombra;

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

    private int _passoDoPixel;

    private static readonly int[] DeslocamentoX = { 0, 2, -1, 1, -2, 0, 1, -1 };
    private static readonly int[] DeslocamentoY = { 0, 1, 2, 0, 1, 2, 1, 0 };

    private bool Mexendo => _cfg.IlhaProtegerTela && _cfg.IlhaMexerPixels;

    private DateTime _vistaEm = DateTime.UtcNow;

    private bool _apagada;
    private bool _escondidaPorInatividade;

    private const double OpacidadeApagada = 0.35;

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

    private void Acordar()
    {
        _vistaEm = DateTime.UtcNow;
        if (_apagada) ConferirBrilho();
    }

    private void ConferirInatividade()
    {
        var kdropOcupado = _pedidoKdrop is not null || _recebendoKdrop is { Estado: EstadoDoPedido.Aceito };
        var parado = _cfg.IlhaProtegerTela && _cfg.IlhaEsconderParado && !kdropOcupado &&
                     TempoParado() >= TimeSpan.FromMinutes(10);

        if (parado && !_escondidaPorInatividade)
        {
            Log.Nota("Kisland: escondida com o PC parado há 10 min");
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
                TrazerParaFrente();
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

    private void OrganizarCartoes()
    {
        var todos = new FrameworkElement[] { CartaoCpu, CartaoGpu, CartaoRam, CartaoDisco, CartaoEnergia };

        foreach (var cartao in todos)
            (cartao.Parent as Panel)?.Children.Remove(cartao);

        var ligados = todos.Where(c => c.Visibility == Visibility.Visible).ToList();
        var naPrimeira = ligados.Count <= 3 ? ligados.Count : 2;

        for (var i = 0; i < ligados.Count; i++)
            (i < naPrimeira ? GradeTemperaturas : GradeSistema).Children.Add(ligados[i]);

        foreach (var cartao in todos.Except(ligados))
            GradeSistema.Children.Add(cartao);

        AjustarFileira(GradeTemperaturas);
        AjustarFileira(GradeSistema);
    }

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

    private Dictionary<FrameworkElement, Thickness>? _margens;

    private void AjustarEspacos()
    {
        var blocos = new FrameworkElement[]
        {
            BlocoMidia, BlocoProgresso, SemMidia, BlocoCalendario, GradeTemperaturas,
            GradeSistema, BlocoVolume, GradePerfis, BlocoAtalhos, BlocoBandeja, BlocoRodape
        };

        _margens ??= blocos.ToDictionary(b => b, b => b.Margin);

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
        BotaoAnel.Margin = new Thickness(0, 0, algum && MidiaCompacta.Visibility == Visibility.Visible ? 8 : 0, 0);

        BotaoPersonalizarPilula.Visibility = Visivel(comBotao);
        Compacta.Margin = comBotao ? new Thickness(6, 0, 7, 0)
                        : algum ? new Thickness(6, 0, 8, 0)
                        : new Thickness(6, 0, 6, 0);
    }

    private void Anel_Click(object sender, RoutedEventArgs e) => AtalhoService.AbrirIa(_cfg);

    private void Posicionar()
    {
        if (_handle == IntPtr.Zero || ActualWidth <= 0 || _arrastando) return;

        try
        {
            var tela = MonitorDaIlha();
            var area = tela.WorkingArea;

            var escala = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (escala <= 0) escala = 1;

            LimitarAltura(area.Height / escala);

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

    private void LimitarAltura(double alturaUtil)
    {
        var teto = Math.Max(200, alturaUtil - 6 - 28 - 2 - 28 - 12);
        if (Math.Abs(RolagemExpandida.MaxHeight - teto) > 0.5) RolagemExpandida.MaxHeight = teto;
    }

    private void RolagemExpandida_Mudou(object sender, ScrollChangedEventArgs e)
    {
        var comBarra = RolagemExpandida.ComputedVerticalScrollBarVisibility == Visibility.Visible;
        var direita = comBarra ? 8 : 16;
        if (Math.Abs(Expandida.Margin.Right - direita) > 0.1)
            Expandida.Margin = new Thickness(20, 6, direita, 6);
    }

    private bool CursorNoVolume()
    {
        DependencyObject? atual = Mouse.DirectlyOver as DependencyObject;
        while (atual is not null)
        {
            if (ReferenceEquals(atual, BlocoVolume)) return true;
            if (ReferenceEquals(atual, RolagemExpandida)) return false;
            atual = atual is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(atual)
                : LogicalTreeHelper.GetParent(atual);
        }

        return false;
    }

    private bool _avisouPorCima;

    private void ConferirPorCima()
    {
        if (_handle == IntPtr.Zero || !_ativa || !IsVisible) return;

        if ((GetWindowLong(_handle, EstiloEstendido) & SempreNaFrente) == 0)
        {
            TrazerParaFrente();
            if (!_avisouPorCima)
            {
                _avisouPorCima = true;
                Log.Nota($"Kisland: tinha perdido o 'sempre por cima' (Kdrop {(_recebendoKdrop is null ? "parado" : "recebendo")}); devolvido");
            }
            return;
        }

        if (QuemCobre() is { } cobre)
        {
            if (!cobre.TelaInteira) TrazerParaFrente();
            if (_cobertasAvisadas.Count < 20 && _cobertasAvisadas.Add(cobre.Chave))
                Log.Nota($"Kisland: estava atrás de {cobre.Descricao}" +
                         (cobre.TelaInteira ? "; tela inteira, ficou atrás" : "; trazida de volta para a frente"));
        }
    }

    private readonly HashSet<string> _cobertasAvisadas = new();

    private void TrazerParaFrente()
    {
        if (_handle == IntPtr.Zero) return;
        SetWindowPos(_handle, PorCimaDeTudo, 0, 0, 0, 0, SemTamanho | SemMover | SemAtivarJanela);
    }

    private static readonly HashSet<string> FrentePermitida = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32768", "tooltips_class32", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "Xaml_WindowedPopupClass", "Progman", "WorkerW", "ForegroundStaging", "MultitaskingViewFrame",
        "TaskSwitcherWnd", "SysShadow", "IME", "MSCTFIME UI"
    };

    private (string Chave, string Descricao, bool TelaInteira)? QuemCobre()
    {
        if (!GetWindowRect(_handle, out var minha)) return null;

        var meuPid = Environment.ProcessId;
        var janela = GetWindow(_handle, AnteriorNaOrdem);
        for (var i = 0; janela != IntPtr.Zero && i < 500; i++, janela = GetWindow(janela, AnteriorNaOrdem))
        {
            if (!IsWindowVisible(janela)) continue;

            GetWindowThreadProcessId(janela, out var pid);
            if (pid == meuPid) continue;

            if (!GetWindowRect(janela, out var r) ||
                r.Direita <= minha.Esquerda || r.Esquerda >= minha.Direita ||
                r.Baixo <= minha.Topo || r.Topo >= minha.Baixo)
                continue;

            if (DwmGetWindowAttribute(janela, Encoberta, out var encoberta, sizeof(int)) == 0 && encoberta != 0) continue;

            var estilo = GetWindowLong(janela, EstiloEstendido);
            if ((estilo & TransparenteAoClique) != 0) continue;

            var classe = new System.Text.StringBuilder(256);
            GetClassName(janela, classe, classe.Capacity);
            if (FrentePermitida.Contains(classe.ToString())) continue;

            string processo;
            try { processo = System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch { processo = $"pid {pid}"; }

            var porCima = (estilo & SempreNaFrente) != 0 ? "sempre por cima" : "comum";
            var tela = MonitorDaIlha().Bounds;
            var telaInteira = r.Esquerda <= tela.Left && r.Topo <= tela.Top &&
                              r.Direita >= tela.Right && r.Baixo >= tela.Bottom;
            return ($"{processo}|{classe}",
                    $"{processo} (classe {classe}, janela {porCima}, {r.Direita - r.Esquerda}×{r.Baixo - r.Topo} em {r.Esquerda},{r.Topo})",
                    telaInteira);
        }

        return null;
    }

    private const uint AnteriorNaOrdem = 3;
    private const int Encoberta = 14;
    private const int TransparenteAoClique = 0x20;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr janela, uint relacao);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr janela);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr janela, System.Text.StringBuilder classe, int tamanho);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr janela, int atributo, out int valor, int tamanho);

    private const int SempreNaFrente = 0x00000008;

    private WF.Screen MonitorDaIlha()
    {
        var escolhido = string.IsNullOrEmpty(_cfg.IlhaMonitor)
            ? null
            : WF.Screen.AllScreens.FirstOrDefault(t => t.DeviceName == _cfg.IlhaMonitor);

        return escolhido ?? WF.Screen.PrimaryScreen ?? WF.Screen.AllScreens[0];
    }

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

        _exigirSaida = true;
        Posicionar();
    }

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

    private void NaoRoubarFoco()
    {
        _handle = new WindowInteropHelper(this).Handle;
        var estilo = GetWindowLong(_handle, EstiloEstendido);
        SetWindowLong(_handle, EstiloEstendido, estilo | SemAtivar | JanelaDeFerramenta);
    }

    private void Cursor_Tick()
    {
        if (!IsVisible || _arrastando) return;

        var agora = DateTime.UtcNow;
        var dentro = CursorEmCima();

        if (ConferirArrastoDeFora(dentro, agora)) return;

        if (dentro || CursorEmCima(60)) Acordar();

        if (_exigirSaida)
        {
            if (dentro) return;
            _exigirSaida = false;
        }

        if (dentro)
        {
            _foraDesde = null;

            if (!_expandida && !_modoJogo && _cfg.IlhaAbrirAoPassarMouse && !BotaoEsquerdoApertado())
            {
                _dentroDesde ??= agora;
                if (agora - _dentroDesde >= EsperaParaAbrir) Expandir();
            }

            return;
        }

        _dentroDesde = null;

        if (!_expandida) return;

        if (BotaoEsquerdoApertado())
        {
            _foraDesde = null;
            return;
        }

        if (_pedidoKdrop is not null)
        {
            _foraDesde = null;
            return;
        }

        _foraDesde ??= agora;
        if (agora - _foraDesde >= EsperaParaFechar) Recolher();
    }

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
            return false;
        }
    }

    private static bool BotaoEsquerdoApertado() => (GetAsyncKeyState(TeclaBotaoEsquerdo) & 0x8000) != 0;

    private static readonly TimeSpan EsperaParaSoltar = TimeSpan.FromMilliseconds(250);

    private bool _botaoAntes;
    private bool _apertoDeFora;
    private DateTime? _arrastoDentroDesde;

    private bool _soltandoNaBandeja;

    private const string DicaBandejaPadrao = "Arraste arquivos até a Kisland para deixá-los aqui, à mão.";

    private bool ConferirArrastoDeFora(bool dentro, DateTime agora)
    {
        var apertado = BotaoEsquerdoApertado();

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

    private void TerminarSoltura(bool dentro)
    {
        _soltandoNaBandeja = false;
        DicaBandeja.Text = DicaBandejaPadrao;

        if (!dentro) Recolher();
        if (_pedidoKdrop is null) AplicarConfiguracao();
        else MontarBandeja();
    }

    private static bool CursorDeArrasto()
    {
        var info = new InfoDoCursor { Tamanho = Marshal.SizeOf<InfoDoCursor>() };
        if (!GetCursorInfo(ref info) || info.Cursor == IntPtr.Zero) return true;

        foreach (var comum in CursoresComuns)
            if (info.Cursor == LoadCursor(IntPtr.Zero, comum))
                return false;

        return true;
    }

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

    private void Relogio_Tick()
    {
        _segundos++;

        if (_segundos % 2 == 0) ConferirPorCima();

        if (_segundos % 5 == 0)
        {
            var antes = _volume.Disponivel;
            _volume.Conferir();
            if (antes != _volume.Disponivel) AplicarConfiguracao();
        }

        if (_segundos % 2 == 0 && _expandida && MixerDeSom.Visibility == Visibility.Visible) MontarMixer();

        AtualizarFps();
        ConferirTelaCheia();
        ConferirInatividade();
        ConferirBrilho();
        ConferirPedidoKdrop();

        if (Mexendo && _segundos % 180 == 0)
        {
            _passoDoPixel++;
            Posicionar();
        }

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

            if (dia.Month != hoje.Month) numero.Opacity = 0.55;

            Grid.SetRow(numero, linha);
            Grid.SetColumn(numero, coluna);
            grade.Children.Add(numero);
        }
    }

    private void MontarAtalhos()
    {
        GradeAtalhos.Children.Clear();

        if (AtalhoService.IaEscolhida(_cfg) is { } ia)
            GradeAtalhos.Children.Add(BotaoDeAtalho(ia.Nome, AtalhoService.MarcaDaIa, ehIa: true));

        foreach (var atalho in _cfg.IlhaAtalhos.Take(AtalhoService.Maximo))
            GradeAtalhos.Children.Add(BotaoDeAtalho(atalho.Nome, atalho.Destino, ehIa: false));

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

    private void ConferirTelaCheia()
    {
        var estado = TelaCheia.Consultar(_handle);
        var meuMonitor = TelaCheia.MonitorDe(_handle);

        var aqui = estado.Exclusiva
            ? _jogo is not { } j || TelaCheia.MonitorDe(j.Janela) == meuMonitor
            : estado.Janela != IntPtr.Zero && estado.Monitor == meuMonitor;

        bool ehOJogo;
        if (!_cfg.IlhaFps || _jogo is not { } jogo)
            ehOJogo = false;
        else if (estado.Exclusiva)
            ehOJogo = _cfg.IlhaFicarEmTelaCheia && ProcessoDaJanela(PrimeiroPlano()) == jogo.Pid;
        else
            ehOJogo = estado.Janela != IntPtr.Zero && ProcessoDaJanela(estado.Janela) == jogo.Pid;

        var modoJogo = aqui && ehOJogo;
        var esconder = aqui && !modoJogo && _cfg.IlhaEsconderEmTelaCheia;

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
            Log.Nota($"Kisland: escondida por tela cheia ({TelaCheia.Descrever(_handle, estado)})");
            _escondidaPorTelaCheia = true;
            _expandida = false;
            _aviso = Aviso.Nenhum;
            MostrarEstado();
            Hide();
        }
        else if (!esconder && _escondidaPorTelaCheia)
        {
            _escondidaPorTelaCheia = false;

            if (!_escondidaPorInatividade)
            {
                Show();
                TrazerParaFrente();
                Posicionar();
                MostrarEstado();
            }
        }
    }

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

    private void OuvirSaida()
    {
        if (_midia.Atual is not { Tocando: true }) return;

        var pico = _volume.Pico();
        if (pico is null || pico > LimiarDeSom) _somDaMidiaEm = DateTime.UtcNow;
    }

    private const float LimiarDeSom = 0.001f;

    private void ConferirSilencio()
    {
        if (_faixaDispensada is not null) return;
        if (_midia.Atual is not { } estado) return;
        if (DateTime.UtcNow - _somDaMidiaEm < SilencioQueLimpa) return;

        _faixaDispensada = Chave(estado);
        _tocandoAoDispensar = estado.Tocando;
        AplicarMidia(false);
    }

    private void MostrarEstado()
    {
        var aviso = _expandida ? Aviso.Nenhum : _aviso;

        Expandida.Visibility = Visivel(_expandida);
        RolagemExpandida.Visibility = Expandida.Visibility;
        if (!_expandida) RolagemExpandida.ScrollToTop();
        AvisoVolume.Visibility = Visivel(aviso == Aviso.Volume);
        AvisoFaixa.Visibility = Visivel(aviso == Aviso.Faixa);
        AvisoTexto.Visibility = Visivel(aviso == Aviso.Texto);
        Compacta.Visibility = Visivel(!_expandida && aviso == Aviso.Nenhum);

        Casca.CornerRadius = new CornerRadius(_expandida ? 16 : aviso is Aviso.Faixa or Aviso.Texto ? 28 : 22);

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

    private static string Rotulo(string peca, float? temperatura, float? uso)
    {
        var partes = new List<string>(2);
        if (temperatura is > 0) partes.Add($"{temperatura:F0}°");
        if (uso is { } u) partes.Add($"{u:F0}%");

        return partes.Count == 0 ? $"{peca} --" : $"{peca} {string.Join(" · ", partes)}";
    }

    private static string Chave(EstadoDeMidia e) => e.Origem + "\u0001" + e.Titulo + "\u0001" + e.Artista;

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

        PorCapa(CapaPequena, NotaPequena, AppPequeno, estado);
        PorCapa(CapaGrande, NotaGrande, AppGrande, estado);

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

        var chave = estado is null ? null : Chave(estado);
        if (veioDeAviso && chave is not null && _ultimaFaixa is not null && chave != _ultimaFaixa &&
            estado!.Tocando && _cfg.IlhaAvisoDeFaixa)
        {
            TituloAviso.Text = estado.Titulo;
            ArtistaAviso.Text = estado.Artista;
            PorCapa(CapaAviso, NotaAviso, AppAviso, estado);
            MostrarAviso(Aviso.Faixa, TimeSpan.FromSeconds(3));
        }

        if (chave is not null) _ultimaFaixa = chave;

        AtualizarSemItens();
        if (_expandida) AtualizarProgresso();
        MostrarEstado();
    }

    private static void PorCapa(Border quadro, System.Windows.Shapes.Path nota, Image app, EstadoDeMidia? estado)
    {
        if (estado?.Capa is { } capa)
        {
            quadro.Background = new ImageBrush(capa) { Stretch = Stretch.UniformToFill };
            nota.Visibility = Visibility.Collapsed;
            app.Visibility = Visibility.Collapsed;
            return;
        }

        quadro.SetResourceReference(Border.BackgroundProperty, "Chip");
        app.Source = estado?.IconeDoApp;
        app.Visibility = Visivel(estado?.IconeDoApp is not null);
        nota.Visibility = Visivel(estado?.IconeDoApp is null);
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

    private int _rodaAcumulada;

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

    private bool GirarRoda(int giro)
    {
        if (_expandida && RolagemExpandida.ScrollableHeight > 0 && !CursorNoVolume() && LinhaSobOCursor() is null)
        {
            RolagemExpandida.ScrollToVerticalOffset(RolagemExpandida.VerticalOffset - giro * 48.0 / 120);
            return true;
        }

        if (!_cfg.IlhaRodaMudaVolume || _modoJogo || !_volume.Disponivel) return false;

        _rodaAcumulada += giro;
        var passos = _rodaAcumulada / 120;
        if (passos == 0) return true;
        _rodaAcumulada -= passos * 120;

        if (_expandida && LinhaSobOCursor() is { } linha)
        {
            linha.Controle.Value = Math.Clamp(linha.Controle.Value + passos * PassoDaRoda, 0, 100);
            return true;
        }

        var nivel = Math.Clamp(_volume.Nivel + passos * PassoDaRoda, 0, 100);

        var mudo = _volume.Mudo;
        if (mudo && passos > 0)
        {
            _volume.AlternarMudo();
            mudo = false;
        }

        _volume.Definir(nivel);

        AplicarVolume(nivel, mudo, false);
        if (!_expandida) MostrarAviso(Aviso.Volume, TimeSpan.FromSeconds(1.5));

        return true;
    }

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

        AplicarVolume(_volume.Nivel, _volume.Mudo, false);
        MontarSaidas();
    }

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

    private readonly List<string> _bandeja = new();

    private const int MaximoNaBandeja = 12;

    private Point? _inicioDoArrasto;
    private DispatcherTimer? _fimDoRecado;

    private void PrepararBandeja()
    {
        try
        {
            var revogado = RevokeDragDrop(_handle);
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

        foreach (var caminho in novos)
        {
            _bandeja.Remove(caminho);
            _bandeja.Add(caminho);
        }
        while (_bandeja.Count > MaximoNaBandeja) _bandeja.RemoveAt(0);

        MontarBandeja();
        AtualizarSemItens();
        MostrarEstado();

        if (!_expandida && !_modoJogo) Expandir();
    }

    private void MontarBandeja()
    {
        _bandeja.RemoveAll(c => !TextoNaBandeja.Eh(c) && !System.IO.File.Exists(c) && !System.IO.Directory.Exists(c));

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

        _kdrop?.BandejaMudou();
    }

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
            1 => "Bandeja · 1 item",
            _ => $"Bandeja · {quantos} itens"
        };
    }

    private const string DesenhoLink =
        "M10,14 A4,4 0 0,0 15.7,14 L18.7,11 A4,4 0 0,0 13,5.3 L12,6.3 M14,10 A4,4 0 0,0 8.3,10 L5.3,13 A4,4 0 0,0 11,18.7 L12,17.7";
    private const string DesenhoTexto = "M4,7 L4,5 L16,5 L16,7 M10,5 L10,19 M8,19 L12,19 M14,12 L20,12 M17,12 L17,19";

    private UIElement ItemDaBandeja(string caminho)
    {
        if (TextoNaBandeja.Eh(caminho)) return ItemDeTextoDaBandeja(caminho);

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

        if (!pasta && EhFotoOuVideo(caminho)) _ = PorMiniatura(circulo, caminho);
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

        if (TextoNaBandeja.Eh(caminho))
        {
            var texto = TextoNaBandeja.De(caminho);
            if (TextoNaBandeja.EhLink(texto))
            {
                Recolher();
                AbrirLink(texto);
            }
            else if (CopiarTexto(texto))
            {
                Recado("Texto copiado! Cole com Ctrl+V");
            }
            return;
        }

        Recolher();
        AtalhoService.Abrir(caminho);
    }

    private UIElement ItemDeTextoDaBandeja(string item)
    {
        var texto = TextoNaBandeja.De(item);
        var link = TextoNaBandeja.EhLink(texto);

        var circulo = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        circulo.SetResourceReference(Border.BackgroundProperty, "Chip");
        circulo.SetResourceReference(Border.BorderBrushProperty, "Acento");

        var desenho = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(link ? DesenhoLink : DesenhoTexto),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 17,
            Height = 17,
            Stretch = Stretch.Uniform
        };
        desenho.SetResourceReference(Shape.StrokeProperty, "Acento");
        circulo.Child = desenho;

        var rotulo = new TextBlock
        {
            Text = TextoNaBandeja.Resumo(texto, 30),
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

        var amostra = texto.Length > 300 ? texto[..300] + "…" : texto;
        var botao = new Button
        {
            Style = (Style)FindResource("BotaoAtalho"),
            Content = conteudo,
            Tag = item,
            ToolTip = amostra + (link
                ? "\nClique para abrir · botão direito copia"
                : "\nClique para copiar")
        };
        botao.Click += ItemDaBandeja_Click;
        botao.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (CopiarTexto(texto)) Recado(link ? "Link copiado!" : "Texto copiado! Cole com Ctrl+V");
        };
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
            Tag = item,
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

    private static readonly HashSet<string> FotosEVideos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".avif",
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".webm", ".3gp"
    };

    private static bool EhFotoOuVideo(string caminho) => FotosEVideos.Contains(System.IO.Path.GetExtension(caminho));

    private readonly Dictionary<string, (DateTime Data, ImageSource? Imagem)> _miniaturas = new(StringComparer.OrdinalIgnoreCase);

    private async Task PorMiniatura(Border quadrado, string caminho)
    {
        DateTime data;
        try { data = System.IO.File.GetLastWriteTimeUtc(caminho); }
        catch { return; }

        if (!_miniaturas.TryGetValue(caminho, out var guardada) || guardada.Data != data)
        {
            var imagem = await Task.Run(() => IconeDoWindows.Miniatura(caminho) ?? MiniaturaPeloWpf(caminho));
            guardada = (data, imagem);
            _miniaturas[caminho] = guardada;
            while (_miniaturas.Count > 64) _miniaturas.Remove(_miniaturas.Keys.First());
        }

        if (guardada.Imagem is null) return;

        quadrado.Child = null;
        quadrado.Background = new ImageBrush(guardada.Imagem) { Stretch = Stretch.UniformToFill };
    }

    private static ImageSource? MiniaturaPeloWpf(string caminho)
    {
        try
        {
            var imagem = new System.Windows.Media.Imaging.BitmapImage();
            imagem.BeginInit();
            imagem.UriSource = new Uri(caminho);
            imagem.DecodePixelWidth = 96;
            imagem.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            imagem.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            imagem.EndInit();
            imagem.Freeze();
            return imagem;
        }
        catch
        {
            return null;
        }
    }

    private static bool CopiarTexto(string texto)
    {
        try
        {
            Clipboard.SetText(texto);
            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: copiar texto", ex);
            return false;
        }
    }

    private static void AbrirLink(string link)
    {
        if (!TextoNaBandeja.EhLink(link)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.Trim()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: abrir link", ex);
        }
    }

    private void Recado(string texto)
    {
        TituloBandeja.Text = texto;
        _fimDoRecado?.Stop();
        _fimDoRecado = Temporizador(TimeSpan.FromSeconds(2.5), () =>
        {
            _fimDoRecado?.Stop();
            TituloDaBandeja();
        });
        _fimDoRecado.Start();
    }

    private void PorTextoNaBandeja(string texto)
    {
        var item = TextoNaBandeja.Item(texto);
        if (TextoNaBandeja.De(item).Length == 0) return;

        _bandeja.Remove(item);
        _bandeja.Add(item);
        while (_bandeja.Count > MaximoNaBandeja) _bandeja.RemoveAt(0);

        MontarBandeja();
        AtualizarSemItens();
        MostrarEstado();
    }

    private void ColarNaBandeja_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var arquivos = Clipboard.GetFileDropList().Cast<string>().ToList();
                foreach (var caminho in arquivos)
                {
                    _bandeja.Remove(caminho);
                    _bandeja.Add(caminho);
                }
                while (_bandeja.Count > MaximoNaBandeja) _bandeja.RemoveAt(0);

                MontarBandeja();
                AtualizarSemItens();
                MostrarEstado();
                return;
            }

            var texto = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (string.IsNullOrWhiteSpace(texto))
            {
                Recado("Nada copiado para colar");
                return;
            }

            PorTextoNaBandeja(texto);
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: colar", ex);
            Recado("Não deu para colar agora");
        }
    }

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

        if (TextoNaBandeja.Eh(caminho))
        {
            botao.ReleaseMouseCapture();
            try
            {
                DragDrop.DoDragDrop(botao, new DataObject(DataFormats.UnicodeText, TextoNaBandeja.De(caminho)), DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                Log.Erro("Bandeja: arrastar texto", ex);
            }
            return;
        }

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

        MontarBandeja();
        AtualizarSemItens();
    }

    private KdropService? _kdrop;

    private PedidoKdrop? _pedidoKdrop;

    private PedidoKdrop? _recebendoKdrop;

    private bool _soBandeja;

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

    private bool KdropLigado => _ativa && _cfg.IlhaBandeja && _cfg.Kdrop;

    private void AtualizarKdrop()
    {
        if (KdropLigado && _kdrop is null)
        {
            _kdrop = new KdropService(_cfg, () =>
            {
                try { return Dispatcher.Invoke(() => _bandeja.ToList()); }
                catch { return new List<string>(); }
            });
            _kdrop.PedidoChegou += p => Dispatcher.BeginInvoke(() => MostrarPedido(p));
            _kdrop.PedidoMudou += p => Dispatcher.BeginInvoke(() => PedidoMudou(p));
            _kdrop.ArquivoRecebido += c => Dispatcher.BeginInvoke(() => ReceberDoKdrop(c));
            _kdrop.AparelhosMudaram += () => Dispatcher.BeginInvoke(() => MontarAparelhos());
            _kdrop.BandejasMudaram += () => Dispatcher.BeginInvoke(() => MontarBandejasDeFora());
            _kdrop.TextoRecebido += p => Dispatcher.BeginInvoke(() => ReceberTextoDoKdrop(p));
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
        FecharTextoKdrop();
        QrKdrop.Visibility = Visibility.Collapsed;
        RedePublicaKdrop.Visibility = Visibility.Collapsed;
        AreaKdrop.Visibility = Visibility.Collapsed;
    }

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

    private readonly Dictionary<string, string> _buscando = new();

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

        var ehTexto = item.Texto is not null;
        var ehLink = ehTexto && TextoNaBandeja.EhLink(item.Texto!);
        var desenho = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(ehLink ? DesenhoLink : ehTexto ? DesenhoTexto : item.Pasta
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
            ToolTip = ehTexto
                ? $"{item.Texto}\nNa bandeja de {pc.Nome}. Clique para trazer e copiar"
                : $"{item.Nome} · {detalhe}\nNa bandeja de {pc.Nome}. Clique para trazer para este PC"
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

        if (item.Texto is not null)
        {
            Mostrar("0%");
            var texto = await _kdrop.BuscarTextoDePc(pc, item);
            if (texto is null)
            {
                Mostrar("Não veio");
                return;
            }

            Mostrar(null);
            PorTextoNaBandeja(texto);
            if (CopiarTexto(texto)) Recado("Texto copiado! Cole com Ctrl+V");
            return;
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

    private async Task MandarPeloKdrop(AparelhoKdrop aparelho, List<string> caminhos)
    {
        if (_kdrop is null) return;

        if (!aparelho.EhPc)
        {
            var paraOCelular = caminhos.Where(c => TextoNaBandeja.Eh(c) || System.IO.File.Exists(c)).ToList();
            if (paraOCelular.Count == 0)
            {
                Situacao(aparelho.Id, "Bandeja vazia");
                return;
            }

            _kdrop.AvisarCelular(aparelho.Id, paraOCelular);
            Situacao(aparelho.Id, "Enviado");
            return;
        }

        var arquivos = caminhos.Where(c => !TextoNaBandeja.Eh(c) && System.IO.File.Exists(c)).ToList();
        if (arquivos.Count == 0)
        {
            Situacao(aparelho.Id, "Bandeja vazia");
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
            RedePublicaKdrop.Visibility = Visibility.Collapsed;
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

        var fixo = _kdrop?.EnderecoFixo;
        FixoKdrop.Text = fixo is null ? "" : $"Endereço fixo, para salvar nos favoritos: {fixo.Replace("http://", "").TrimEnd('/')}";
        FixoKdrop.Visibility = Visivel(fixo is not null);

        _ = ConferirRedePublica();
        QrKdrop.Visibility = Visibility.Visible;
        MontarAparelhos();
    }

    private async Task ConferirRedePublica()
    {
        var d = await Task.Run(() => KdropService.Diagnosticar());
        if (d is null || QrKdrop.Visibility != Visibility.Visible)
        {
            RedePublicaKdrop.Visibility = Visibility.Collapsed;
            return;
        }

        var motivos = new List<string>();
        if (!d.Respondendo)
            motivos.Add("O Kdrop não está respondendo neste PC. Desligue e ligue o Kdrop na Personalização; se continuar, reinicie o KInfo.");
        if (d.Categoria == "Public")
            motivos.Add("O Windows marcou esta rede como Pública, e nela bloqueia o acesso de outros aparelhos. Esse costuma ser o motivo de a página ficar carregando. Se é a rede da sua casa, deixe-a como Privada.");
        if (d.BloqueiaTudo)
            motivos.Add("O Firewall do Windows está em \"Bloquear todas as conexões de entrada\". Desmarque essa opção em Segurança do Windows › Firewall e proteção de rede, na rede em uso.");
        if (d.FirewallDeFora is { } produto)
            motivos.Add($"O {produto} tem firewall próprio, que pode barrar o celular. Se a página não abrir, libere o KInfo (ou a porta {KdropService.Porta}) nele.");

        TextoRedePublicaKdrop.Text = string.Join("\n\n", motivos);
        BotaoRedePrivada.Visibility = Visivel(d.Categoria == "Public");
        RedePublicaKdrop.Visibility = Visivel(motivos.Count > 0);
    }

    private async void TornarRedePrivada_Click(object sender, RoutedEventArgs e)
    {
        BotaoRedePrivada.IsEnabled = false;
        BotaoRedePrivada.Content = "Mudando…";

        var deu = await Task.Run(() => KdropService.TornarRedePrivada());

        BotaoRedePrivada.IsEnabled = true;
        BotaoRedePrivada.Content = "Tornar a rede Privada";
        if (deu)
        {
            RedePublicaKdrop.Visibility = Visibility.Collapsed;
            await ConferirRedePublica();
            return;
        }

        TextoRedePublicaKdrop.Text = "O Windows não deixou mudar por aqui. Abra Configurações › Rede e Internet › Wi-Fi (ou Ethernet), " +
                                     "toque na rede e escolha Rede privada.";
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

    private void MostrarPedido(PedidoKdrop pedido)
    {
        _pedidoKdrop = pedido;

        if (pedido.Texto is { } texto)
        {
            var link = TextoNaBandeja.EhLink(texto);
            TituloPedidoKdrop.Text = $"{pedido.OrigemNome} quer mandar {(link ? "um link" : "um texto")}";
            DetalhePedidoKdrop.Text = TextoNaBandeja.Resumo(texto, 120);
        }
        else
        {
            var n = pedido.Arquivos.Count;
            TituloPedidoKdrop.Text = $"{pedido.OrigemNome} quer colocar {(n == 1 ? "1 arquivo" : $"{n} arquivos")} na sua bandeja";

            var nomes = string.Join(", ", pedido.Arquivos.Take(3).Select(a => a.Nome));
            if (n > 3) nomes += $" e mais {n - 3}";
            DetalhePedidoKdrop.Text = $"{nomes} · {Tamanho(pedido.Total)}";
        }

        IconePedidoKdrop.Data = Geometry.Parse(pedido.OrigemEhPc ? DesenhoDesktop : DesenhoCelular);
        LembrarPedidoKdrop.IsChecked = false;
        PedidoKdrop.Visibility = Visibility.Visible;
        _soBandeja = true;

        if (!_ativa || _escondidaPorTelaCheia) return;

        if (_escondidaPorInatividade)
        {
            _escondidaPorInatividade = false;
            Show();
            TrazerParaFrente();
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

    private void ConferirPedidoKdrop()
    {
        if (_pedidoKdrop is { } pedido && DateTime.UtcNow - pedido.CriadoEm > KdropService.PrazoDoPedido)
            ResponderPedido(false);
    }

    private void PedidoMudou(PedidoKdrop pedido)
    {
        if (_pedidoKdrop?.Id == pedido.Id && pedido.Estado != EstadoDoPedido.Esperando)
            FecharPedido();

        if (pedido.Estado == EstadoDoPedido.Aceito) _recebendoKdrop = pedido;
        else if (_recebendoKdrop?.Id == pedido.Id) _recebendoKdrop = null;

        TituloDaBandeja();
        AtualizarChipBandeja();
        AtualizarSemItens();
        MostrarEstado();
    }

    private void ReceberDoKdrop(string caminho)
    {
        _bandeja.Remove(caminho);
        _bandeja.Add(caminho);
        while (_bandeja.Count > MaximoNaBandeja) _bandeja.RemoveAt(0);

        MontarBandeja();
        AtualizarSemItens();
        MostrarEstado();
    }

    private string? _textoKdrop;
    private DispatcherTimer? _fimDoTextoKdrop;

    private void ReceberTextoDoKdrop(PedidoKdrop pedido)
    {
        if (pedido.Texto is not { } texto) return;

        PorTextoNaBandeja(texto);

        var link = TextoNaBandeja.EhLink(texto);
        _textoKdrop = texto;
        var origem = $"{(link ? "Link" : "Texto")} de {pedido.OrigemNome} · Kdrop";
        var desenho = Geometry.Parse(link ? DesenhoLink : DesenhoTexto);

        OrigemTextoKdrop.Text = origem;
        ConteudoTextoKdrop.Text = texto.Length > 500 ? texto[..500] + "…" : texto;
        IconeTextoKdrop.Data = desenho;
        CopiarTextoKdrop.Content = "Copiar";
        AbrirTextoKdrop.Visibility = Visivel(link);
        ColunaAbrirTextoKdrop.Width = link ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TextoKdrop.Visibility = Visibility.Visible;

        OrigemAvisoTexto.Text = origem;
        ConteudoAvisoTexto.Text = TextoNaBandeja.Resumo(texto, 80);
        IconeAvisoTexto.Data = desenho;
        CopiarAvisoTexto.Content = "Copiar";

        _fimDoTextoKdrop?.Stop();
        _fimDoTextoKdrop = Temporizador(TimeSpan.FromSeconds(20), FecharTextoKdrop);
        _fimDoTextoKdrop.Start();

        if (!_expandida) MostrarAviso(Aviso.Texto, TimeSpan.FromSeconds(6));
    }

    private void FecharTextoKdrop()
    {
        _fimDoTextoKdrop?.Stop();
        _textoKdrop = null;
        TextoKdrop.Visibility = Visibility.Collapsed;
        if (_aviso == Aviso.Texto) EncerrarAviso();
    }

    private void FecharTextoKdrop_Click(object sender, RoutedEventArgs e) => FecharTextoKdrop();

    private void CopiarTextoKdrop_Click(object sender, RoutedEventArgs e)
    {
        if (_textoKdrop is not { } texto || !CopiarTexto(texto)) return;

        CopiarTextoKdrop.Content = "Copiado";
        CopiarAvisoTexto.Content = "Copiado";

        if (_aviso == Aviso.Texto)
        {
            _fimDoAviso.Stop();
            _fimDoAviso.Interval = TimeSpan.FromSeconds(1.2);
            _fimDoAviso.Start();
        }
    }

    private void AbrirTextoKdrop_Click(object sender, RoutedEventArgs e)
    {
        if (_textoKdrop is not { } texto) return;

        AbrirLink(texto);
        FecharTextoKdrop();
        Recolher();
    }

    private static string Tamanho(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
    };

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

    private void CopiarBandeja_Click(object sender, RoutedEventArgs e)
    {
        if (_bandeja.Count == 0) return;

        try
        {
            var arquivos = _bandeja.Where(c => !TextoNaBandeja.Eh(c)).ToArray();
            if (arquivos.Length > 0)
            {
                var lista = new System.Collections.Specialized.StringCollection();
                lista.AddRange(arquivos);
                Clipboard.SetFileDropList(lista);
            }
            else
            {
                Clipboard.SetText(string.Join(Environment.NewLine, _bandeja.Select(TextoNaBandeja.De)));
            }

            Recado("Copiado! Cole com Ctrl+V");
        }
        catch (Exception ex)
        {
            Log.Erro("Bandeja: copiar", ex);
        }
    }

    private void LimparBandeja_Click(object sender, RoutedEventArgs e)
    {
        _bandeja.Clear();
        MontarBandeja();
        AtualizarSemItens();
    }

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
