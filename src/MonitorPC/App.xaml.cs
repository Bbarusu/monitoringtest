using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MonitorPC.Services;
using MonitorPC.Views;
using D = System.Drawing;
using WF = System.Windows.Forms;

namespace MonitorPC;

public partial class App : Application
{
    private const string ArgumentoElevado = "--elevado";

    private const string NomeDaTarefa = "KInfo";

    private bool SubirPelaTarefa(string[] argumentos)
    {
        try
        {
            if (argumentos.Any(a => a.Equals(ArgumentoElevado, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (Elevado() || !TarefaExiste()) return false;

            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{NomeDaTarefa}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (p is null) return false;

            p.WaitForExit(10000);

            if (p.ExitCode != 0)
            {
                Log.Erro("Subir pela tarefa",
                    new InvalidOperationException($"schtasks devolveu {p.ExitCode}"));
                return false;
            }

            Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Subir pela tarefa", ex);
            return false;
        }
    }

    private static D.Icon IconeDoApp()
    {
        try
        {
            return D.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? D.SystemIcons.Application;
        }
        catch (Exception ex)
        {
            Log.Erro("Ícone do app", ex);
            return D.SystemIcons.Application;
        }
    }

    private static bool Elevado()
    {
        using var identidade = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identidade)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool TarefaExiste()
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Query /TN \"{NomeDaTarefa}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (p is null) return false;

        p.WaitForExit(10000);
        return p.ExitCode == 0;
    }

    private AppConfig _cfg = null!;
    private SensorService _sensores = null!;
    private AlertService _alertas = null!;
    private WF.NotifyIcon _bandeja = null!;
    private FlyoutWindow _painel = null!;

    private IlhaWindow? _ilha;
    private MidiaService? _midia;
    private VolumeService? _volume;
    private D.Icon? _iconeAtual;
    private System.Windows.Controls.ContextMenu? _menu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstalarCapturaDeErros();

        if (SubirPelaTarefa(e.Args)) return;

        _cfg = AppConfig.Carregar();
        ThemeService.Aplicar(_cfg.Tema);
        AplicarAcento();

        _sensores = new SensorService(_cfg);
        _alertas = new AlertService(_cfg);
        _painel = new FlyoutWindow(_cfg, _sensores, _alertas);

        _alertas.Comecou += alerta => Dispatcher.BeginInvoke(() => Notificar(alerta));

        _bandeja = new WF.NotifyIcon
        {
            Text = "KInfo",
            Visible = true,
            Icon = IconeDoApp()
        };
        _bandeja.MouseClick += (_, args) =>
        {
            if (args.Button == WF.MouseButtons.Left) _painel.Alternar();
            else if (args.Button == WF.MouseButtons.Right) AbrirMenu();
        };

        MontarMenu();
        _bandeja.BalloonTipClicked += (_, _) => _painel.Mostrar();

        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() =>
        {
            _alertas.Avaliar(snap);
            AtualizarBandeja(snap.CpuTemp, snap.SensoresDeBaixoNivel);
        });

        _sensores.Iniciar();

        if (!_cfg.ConfiguracaoInicialFeita)
        {
            _painel.Mostrar();
            _painel.AbrirConfiguracao(true);
        }
        else if (PedidoDoInstalador())
        {
            _painel.Mostrar();
        }

        AtualizarIlha();
        ConvidarParaAKisland();

        _iniciado = true;

        _ = ProcurarAoIniciar();
    }

    private enum MomentoDaProcura { AoIniciar, DeTempoEmTempo, PedidaPeloUsuario }

    private string? _versaoAvisada;
    private System.Windows.Threading.DispatcherTimer? _procuraPeriodica;

    private async Task ProcurarAoIniciar()
    {
        for (var tentativa = 0; tentativa < 4 && _cfg.VerificarAtualizacoes; tentativa++)
        {
            if (await ProcurarAtualizacao(MomentoDaProcura.AoIniciar)) break;
            await Task.Delay(TimeSpan.FromMinutes(1));
        }

        _procuraPeriodica = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _procuraPeriodica.Tick += (_, _) =>
        {
            if (_cfg.VerificarAtualizacoes) _ = ProcurarAtualizacao(MomentoDaProcura.DeTempoEmTempo);
        };
        _procuraPeriodica.Start();
    }

    private async Task<bool> ProcurarAtualizacao(MomentoDaProcura quando)
    {
        var pedidaPeloUsuario = quando == MomentoDaProcura.PedidaPeloUsuario;

        try
        {
            var procura = await _atualizacoes.ProcurarAsync();

            if (procura.Verificou)
            {
                _cfg.UltimaVerificacaoDeAtualizacao = DateTime.UtcNow;
                _cfg.Salvar();
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (procura.TemNovidade)
                {
                    var manifesto = procura.Manifesto!;
                    _painel.OferecerAtualizacao(manifesto);

                    if (quando != MomentoDaProcura.DeTempoEmTempo) _painel.Mostrar();

                    if (!pedidaPeloUsuario && _versaoAvisada != manifesto.Versao)
                    {
                        _versaoAvisada = manifesto.Versao;
                        _bandeja.BalloonTipTitle = $"KInfo {manifesto.Versao} disponível";
                        _bandeja.BalloonTipText = "Uma nova versão está pronta para instalar. Toque para ver o que mudou.";
                        _bandeja.BalloonTipIcon = WF.ToolTipIcon.Info;
                        _bandeja.ShowBalloonTip(10000);
                    }
                    return;
                }

                if (!pedidaPeloUsuario) return;

                _painel.Mostrar();

                _painel.Recado(procura.Verificou
                    ? $"Você já tem a versão mais recente ({AtualizacaoService.VersaoAtual})."
                    : $"Não foi possível verificar: {procura.Motivo}.");
            });

            return procura.Verificou;
        }
        catch (Exception ex)
        {
            Log.Erro("Procurar atualização", ex);
            return false;
        }
    }

    private readonly AtualizacaoService _atualizacoes = new();

    private bool _iniciado;

    private static bool PedidoDoInstalador()
    {
        try
        {
            var marca = Path.Combine(
                Path.GetDirectoryName(AppConfig.Caminho)!, "abrir-apos-instalar");

            if (!File.Exists(marca)) return false;

            File.Delete(marca);
            return true;
        }
        catch (Exception ex)
        {
            Log.Erro("Marca do instalador", ex);
            return false;
        }
    }

    private void InstalarCapturaDeErros()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Erro("Interface", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Erro("Processo", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Erro("Tarefa em segundo plano", args.Exception);
            args.SetObserved();
        };
    }

    private void MontarMenu()
    {
        _menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)Resources["MenuDaBandeja"]
        };

        _menu.Items.Add(Item("Abrir painel", () => _painel.Mostrar()));
        _menu.Items.Add(Item("Voltar ao canto", () => _painel.VoltarAoCanto()));
        _menu.Items.Add(Item("Ajustes", () =>
        {
            _painel.Mostrar();
            _painel.AbrirConfiguracao(false);
        }));
        _menu.Items.Add(Item("Procurar atualizações", () => _ = ProcurarAtualizacao(MomentoDaProcura.PedidaPeloUsuario)));
        _menu.Items.Add(Item("Salvar relatório do problema", SalvarRelatorio));

        _menu.Items.Add(new System.Windows.Controls.Separator());
        _menu.Items.Add(Item("Encerrar", Shutdown));

        _menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(_menu) is HwndSource fonte)
                SetForegroundWindow(fonte.Handle);
        };

        System.Windows.Controls.MenuItem Item(string texto, Action acao)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = texto,
                Style = (Style)Resources["ItemDoMenu"]
            };
            item.Click += (_, _) => acao();
            return item;
        }
    }

    private void SalvarRelatorio()
    {
        try
        {
            Diagnostico.Revelar(_sensores.SalvarDiagnostico());
        }
        catch (Exception ex)
        {
            Log.Erro("Salvar diagnóstico", ex);
        }
    }

    private void AbrirMenu()
    {
        if (_menu is null) return;

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    public void RecriarIconeDaBandeja()
    {
        try
        {
            _bandeja.Visible = false;
            _bandeja.Visible = true;
        }
        catch (Exception ex)
        {
            Log.Erro("Recriar ícone da bandeja", ex);
        }
    }

    public void AplicarAparencia()
    {
        ThemeService.Aplicar(_cfg.Tema);
        AplicarAcento();
        AtualizarIlha();
    }

    private const int VersaoDoGuia = 2;

    private const int VersaoDasNovidades = 1;

    private void MostrarNovidades()
    {
        try
        {
            _cfg.NovidadesVersaoVista = VersaoDasNovidades;
            _cfg.GuiaKislandVersaoVista = Math.Max(_cfg.GuiaKislandVersaoVista, VersaoDoGuia);
            _cfg.GuiaKislandVisto = true;
            _cfg.Salvar();

            _painel.Mostrar();
            Dispatcher.BeginInvoke(() => _painel.AbrirNovidades(obrigatorio: true),
                                   System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            Log.Erro("Novidades", ex);
        }
    }

    private void ConvidarParaAKisland()
    {
        if (_cfg.NovidadesVersaoVista < VersaoDasNovidades)
        {
            MostrarNovidades();
            return;
        }

        if (_cfg.GuiaKislandVersaoVista >= VersaoDoGuia) return;

        try
        {
            _cfg.GuiaKislandVersaoVista = VersaoDoGuia;
            _cfg.GuiaKislandVisto = true;
            _cfg.Salvar();

            _painel.Mostrar();
            Dispatcher.BeginInvoke(() => _painel.AbrirGuiaKisland(obrigatorio: true),
                                   System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            Log.Erro("Convite da Kisland", ex);
        }
    }

    private void AtualizarIlha()
    {
        try
        {
            if (!_cfg.IlhaAtiva)
            {
                _ilha?.Desativar();
                return;
            }

            if (_ilha is null)
            {
                var primeiraVez = _midia is null;
                _midia ??= new MidiaService();
                _volume ??= new VolumeService();
                _ilha = new IlhaWindow(_cfg, _sensores, _alertas, _midia, _volume);

                _ilha.PedirPainel += () => _painel.Mostrar();
                _ilha.PedirPersonalizacao += () =>
                {
                    _painel.Mostrar();
                    _painel.AbrirPersonalizacao(irParaIlha: true);
                };
                _ilha.PedirDesempenho += () =>
                {
                    _painel.Mostrar();
                    _painel.AbrirDesempenho();
                };

                if (primeiraVez) _ = _midia.IniciarAsync();
                _ilha.Ativar();
                return;
            }

            if (_ilha.Ativa) _ilha.AplicarConfiguracao();
            else _ilha.Ativar();
        }
        catch (Exception ex)
        {
            Log.Erro("Ilha do topo", ex);
        }
    }

    private void AplicarAcento()
    {
        var cor = Colors.DodgerBlue;

        try
        {
            cor = (Color)ColorConverter.ConvertFromString(_cfg.CorDestaque)!;

            if (_cfg.AcompanharCorDoSistema && CorDeDestaqueDoWindows() is { } sistema)
                cor = sistema;
        }
        catch (Exception ex)
        {
            Log.Erro("Cor de destaque", ex);
        }

        Resources["Acento"] = new SolidColorBrush(cor);
    }

    private static Color? CorDeDestaqueDoWindows()
    {
        try
        {
            using var chave = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\DWM");

            if (chave?.GetValue("AccentColor") is not int abgr) return null;

            var cor = Color.FromRgb(
                (byte)(abgr & 0xFF),
                (byte)((abgr >> 8) & 0xFF),
                (byte)((abgr >> 16) & 0xFF));

            var brilho = (0.299 * cor.R + 0.587 * cor.G + 0.114 * cor.B) / 255.0;
            return brilho is > 0.12 and < 0.95 ? cor : null;
        }
        catch
        {
            return null;
        }
    }

    private void AtualizarBandeja(float? cpuTemp, bool temSensores)
    {
        var texto = temSensores && cpuTemp is > 0 ? $"{cpuTemp:F0}" : "--";
        _bandeja.Text = temSensores
            ? $"KInfo — CPU {texto} °C"
            : "KInfo — sensores de baixo nível indisponíveis";

        using var bmp = new D.Bitmap(32, 32, D.Imaging.PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = D.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = D.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(D.Color.Transparent);

            var acento = ((SolidColorBrush)Resources["Acento"]).Color;
            using var fundo = new D.SolidBrush(D.Color.FromArgb(235, 18, 20, 24));
            using var contorno = new D.Pen(
                D.Color.FromArgb(temSensores ? 255 : 90, acento.R, acento.G, acento.B), 2f);

            using var quadro = Quadrado(new D.RectangleF(1, 1, 30, 30), 6);
            g.FillPath(fundo, quadro);
            g.DrawPath(contorno, quadro);

            var tamanho = texto.Length >= 3 ? 16f : 21f;

            using var fonte = new D.Font("Segoe UI", tamanho, D.FontStyle.Bold,
                                         D.GraphicsUnit.Pixel);
            using var branco = new D.SolidBrush(D.Color.White);
            using var centro = new D.StringFormat
            {
                Alignment = D.StringAlignment.Center,
                LineAlignment = D.StringAlignment.Center
            };

            g.DrawString(texto, fonte, branco, new D.RectangleF(0, 0, 32, 33), centro);
        }

        var anterior = _iconeAtual;
        var handle = bmp.GetHicon();
        _iconeAtual = D.Icon.FromHandle(handle);
        _bandeja.Icon = _iconeAtual;

        if (anterior is not null)
        {
            var antigo = anterior.Handle;
            anterior.Dispose();
            DestroyIcon(antigo);
        }
    }

    private void Notificar(Alerta alerta)
    {
        if (!_cfg.NotificarNoWindows) return;

        _bandeja.BalloonTipTitle = alerta.Titulo;
        _bandeja.BalloonTipText = alerta.Detalhe;
        _bandeja.BalloonTipIcon = WF.ToolTipIcon.Warning;
        _bandeja.ShowBalloonTip(8000);
    }

    private static D.Drawing2D.GraphicsPath Quadrado(D.RectangleF area, float raio)
    {
        var caminho = new D.Drawing2D.GraphicsPath();
        var d = raio * 2;

        caminho.AddArc(area.X, area.Y, d, d, 180, 90);
        caminho.AddArc(area.Right - d, area.Y, d, d, 270, 90);
        caminho.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
        caminho.AddArc(area.X, area.Bottom - d, d, d, 90, 90);
        caminho.CloseFigure();

        return caminho;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr janela);

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_iniciado)
        {
            base.OnExit(e);
            return;
        }

        Tentar(() => _bandeja.Visible = false);
        Tentar(() => _bandeja.Dispose());
        Tentar(() => _sensores.Dispose());
        Tentar(() => _ilha?.Close());
        Tentar(() => _midia?.Dispose());
        Tentar(() => _volume?.Dispose());
        Tentar(() => _painel.GravarMaximos());

        Tentar(() => _sensores.Consumo.Gravar());
        Tentar(() => _cfg.Salvar());
        Tentar(() => { if (_iconeAtual is not null) DestroyIcon(_iconeAtual.Handle); });

        base.OnExit(e);

        static void Tentar(Action acao)
        {
            try { acao(); }
            catch (Exception ex) { Log.Erro("Encerrar", ex); }
        }
    }
}
