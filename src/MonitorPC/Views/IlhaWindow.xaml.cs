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

namespace MonitorPC.Views;

/// <summary>
/// A ilha do topo da tela: uma pilula com o que esta tocando, as temperaturas e
/// o volume, que abre no painel de bolso ao passar o mouse.
///
/// Nao le sensor nenhum por conta propria. As temperaturas chegam pela mesma
/// leitura que alimenta o icone da bandeja; musica e volume chegam por aviso do
/// Windows. O unico relogio proprio e o de um segundo, que confere tela cheia e
/// anda a barra de progresso — e so faz a segunda coisa com a ilha aberta.
/// </summary>
public partial class IlhaWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly AlertService _alertas;
    private readonly MidiaService _midia;
    private readonly VolumeService _volume;

    private readonly DispatcherTimer _relogio;
    private readonly DispatcherTimer _abrir;
    private readonly DispatcherTimer _fechar;
    private readonly DispatcherTimer _fimDoAviso;

    private enum Aviso { Nenhum, Volume, Faixa }

    private Aviso _aviso = Aviso.Nenhum;
    private bool _expandida;
    private bool _ativa;
    private bool _escondidaPorTelaCheia;
    private bool _ajustandoVolume;
    private bool _trocandoPerfil;
    private bool _equalizadorAndando;
    private int _segundos;
    private string? _ultimaFaixa;
    private IntPtr _handle;

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
        _abrir = Temporizador(TimeSpan.FromMilliseconds(180), () => { _abrir.Stop(); Expandir(); });
        _fechar = Temporizador(TimeSpan.FromMilliseconds(450), Fechar_Tick);
        _fimDoAviso = Temporizador(TimeSpan.FromSeconds(2), () => { _fimDoAviso.Stop(); EncerrarAviso(); });

        SourceInitialized += (_, _) => NaoRoubarFoco();
        SizeChanged += (_, _) => Posicionar();

        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() => AplicarSensores(snap));
        _midia.Mudou += estado => Dispatcher.BeginInvoke(() => AplicarMidia(estado, true));
        _volume.Mudou += (nivel, mudo) => Dispatcher.BeginInvoke(() => AplicarVolume(nivel, mudo, true));

        AplicarConfiguracao();
    }

    private static DispatcherTimer Temporizador(TimeSpan intervalo, Action acao)
    {
        var t = new DispatcherTimer { Interval = intervalo };
        t.Tick += (_, _) => acao();
        return t;
    }

    // ------------------------------------------------------------------
    // Ligar, desligar e reagir a Personalizacao

    public void Ativar()
    {
        _ativa = true;
        AplicarConfiguracao();

        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AplicarMidia(_midia.Atual, false);
        AplicarVolume(_volume.Nivel, _volume.Mudo, false);

        _escondidaPorTelaCheia = false;
        Show();
        Posicionar();
        _relogio.Start();
    }

    public void Desativar()
    {
        _ativa = false;
        _relogio.Stop();
        _abrir.Stop();
        _fechar.Stop();
        _fimDoAviso.Stop();
        _expandida = false;
        _aviso = Aviso.Nenhum;
        PararEqualizador();
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

        ChipCpu.Visibility = Visivel(_cfg.IlhaCpu);
        ChipGpu.Visibility = Visivel(_cfg.IlhaGpu);
        ChipRam.Visibility = Visivel(_cfg.IlhaRam);
        ChipVolume.Visibility = Visivel(_cfg.IlhaVolume && _volume.Disponivel);
        BlocoVolume.Visibility = Visivel(_volume.Disponivel);

        AplicarMidia(_midia.Atual, false);
        AtualizarSemItens();
    }

    private static Visibility Visivel(bool sim) => sim ? Visibility.Visible : Visibility.Collapsed;

    private void AtualizarSemItens()
    {
        var algum = MidiaCompacta.Visibility == Visibility.Visible ||
                    ChipAlerta.Visibility == Visibility.Visible ||
                    ChipCpu.Visibility == Visibility.Visible ||
                    ChipGpu.Visibility == Visibility.Visible ||
                    ChipRam.Visibility == Visibility.Visible ||
                    ChipVolume.Visibility == Visibility.Visible;

        SemItens.Visibility = Visivel(!algum);
    }

    // ------------------------------------------------------------------
    // Posicao e foco

    /// <summary>
    /// Centro do topo da area util do monitor principal. A janela cresce para
    /// baixo ao abrir e e recentralizada a cada mudanca de tamanho.
    /// </summary>
    private void Posicionar()
    {
        if (ActualWidth <= 0) return;

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top;
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

        if (_expandida) AtualizarProgresso();
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
            PararEqualizador();
            Hide();
        }
        else if (!cheia && _escondidaPorTelaCheia)
        {
            _escondidaPorTelaCheia = false;
            Show();
            AplicarMidia(_midia.Atual, false);
            Posicionar();
        }
    }

    // ------------------------------------------------------------------
    // Abrir e fechar

    private void Casca_MouseEnter(object sender, MouseEventArgs e)
    {
        _fechar.Stop();
        if (_cfg.IlhaAbrirAoPassarMouse && !_expandida) _abrir.Start();
    }

    private void Casca_MouseLeave(object sender, MouseEventArgs e)
    {
        _abrir.Stop();
        if (_expandida) _fechar.Start();
    }

    private void Casca_Clique(object sender, MouseButtonEventArgs e)
    {
        if (_expandida) return;

        _abrir.Stop();
        Expandir();
    }

    private void Fechar_Tick()
    {
        _fechar.Stop();

        // Arrastando o volume o cursor pode sair da ilha sem a pessoa ter
        // terminado: so fecha depois de soltar.
        if (Casca.IsMouseOver || Mouse.Captured is not null)
        {
            _fechar.Start();
            return;
        }

        Recolher();
    }

    private void Expandir()
    {
        if (_expandida) return;

        _expandida = true;
        _aviso = Aviso.Nenhum;
        _fimDoAviso.Stop();

        SincronizarPerfil();
        if (_sensores.Ultimo is { } ultimo) AplicarSensores(ultimo);
        AplicarMidia(_midia.Atual, false);
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
        MostrarEstado();
    }

    /// <summary>Um estado visivel por vez; a janela se ajusta ao que sobrou.</summary>
    private void MostrarEstado()
    {
        var aviso = _expandida ? Aviso.Nenhum : _aviso;

        Expandida.Visibility = Visivel(_expandida);
        AvisoVolume.Visibility = Visivel(aviso == Aviso.Volume);
        AvisoFaixa.Visibility = Visivel(aviso == Aviso.Faixa);
        Compacta.Visibility = Visivel(!_expandida && aviso == Aviso.Nenhum);

        Casca.CornerRadius = new CornerRadius(_expandida ? 16 : aviso == Aviso.Faixa ? 28 : 22);

        // Animacao parada quando ninguem a ve: a pilula e o unico lugar do
        // equalizador.
        if (_ativa && !_escondidaPorTelaCheia && Compacta.Visibility == Visibility.Visible &&
            _midia.Atual is { Tocando: true } && _cfg.IlhaMusica)
            AndarEqualizador();
        else
            PararEqualizador();
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
        TextoCpu.Text = s.CpuTemp is > 0 ? $"CPU {s.CpuTemp:F0}°" : "CPU --";
        TextoGpu.Text = s.GpuTemp is > 0 ? $"GPU {s.GpuTemp:F0}°" : "GPU --";
        TextoRam.Text = s.RamUso is { } ram ? $"RAM {ram:F0}%" : "RAM --";

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

        Rodape.Text = s.SensoresDeBaixoNivel
            ? "Sensores ativos"
            : "Sensores de baixo nível indisponíveis";
    }

    // ------------------------------------------------------------------
    // Midia

    private void AplicarMidia(EstadoDeMidia? estado, bool veioDeAviso)
    {
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
        var chave = estado is null ? null : estado.Titulo + "\u0001" + estado.Artista;
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
        if (_midia.Atual is not { Duracao.TotalSeconds: > 0 } estado) return;

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

    private void TocarPausar_Click(object sender, RoutedEventArgs e) => _ = _midia.TocarPausar();
    private void Proxima_Click(object sender, RoutedEventArgs e) => _ = _midia.Proxima();
    private void Anterior_Click(object sender, RoutedEventArgs e) => _ = _midia.Anterior();

    // ------------------------------------------------------------------
    // Volume

    private const string AltoFalante = "M11,5 L6,9 L2,9 L2,15 L6,15 L11,19 Z M15.5,8.5 A5,5 0 0,1 15.5,15.5";
    private const string AltoFalanteMudo = "M11,5 L6,9 L2,9 L2,15 L6,15 L11,19 Z M16,9 L22,15 M22,9 L16,15";

    private void AplicarVolume(int nivel, bool mudo, bool veioDeAviso)
    {
        var icone = Geometry.Parse(mudo ? AltoFalanteMudo : AltoFalante);
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr janela, int indice);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr janela, int indice, int valor);
}
