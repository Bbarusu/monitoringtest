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
    private AppConfig _cfg = null!;
    private SensorService _sensores = null!;
    private AlertService _alertas = null!;
    private WF.NotifyIcon _bandeja = null!;
    private FlyoutWindow _painel = null!;
    private D.Icon? _iconeAtual;
    private System.Windows.Controls.ContextMenu? _menu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstalarCapturaDeErros();

        _cfg = AppConfig.Carregar();
        ThemeService.Aplicar(_cfg.Tema);
        AplicarAcento();

        _sensores = new SensorService(_cfg);
        _alertas = new AlertService(_cfg);
        _painel = new FlyoutWindow(_cfg, _sensores, _alertas);

        // A notificacao sai uma vez por episodio, nao a cada leitura.
        _alertas.Comecou += alerta => Dispatcher.BeginInvoke(() => Notificar(alerta));

        _bandeja = new WF.NotifyIcon
        {
            Text = "Monitor de PC",
            Visible = true,
            Icon = D.SystemIcons.Application
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

        // Na primeira execucao o painel abre direto na configuracao, para os
        // limites de temperatura nao ficarem no palpite do padrao.
        if (!_cfg.ConfiguracaoInicialFeita)
        {
            _painel.Mostrar();
            _painel.AbrirConfiguracao(true);
        }
    }

    /// <summary>
    /// Sem isto, qualquer excecao vira uma caixa de erro do Windows sem
    /// informacao nenhuma. Erros na interface sao registrados e o app segue;
    /// os demais sao registrados antes de encerrar.
    /// </summary>
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

    /// <summary>
    /// O acento acompanha a cor do Windows quando a configuracao pede;
    /// senao usa a cor escolhida pelo usuario.
    /// </summary>
    /// <summary>
    /// O menu da bandeja e do WPF, nao do WinForms: o menu do WinForms depende de
    /// o app virar primeiro plano para aparecer, e com um jogo em tela cheia o
    /// Windows nega isso, deixando o menu atras da barra de tarefas.
    /// </summary>
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
        _menu.Items.Add(new System.Windows.Controls.Separator());
        _menu.Items.Add(Item("Encerrar", Shutdown));

        // Sem isto o menu so fecha ao escolher algo, quando o app nao esta em
        // primeiro plano.
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

    private void AbrirMenu()
    {
        if (_menu is null) return;

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>
    /// Reinstala o icone na bandeja. O WinForms costuma refaze-lo sozinho quando
    /// o Explorer recria a area de notificacao, mas isto garante o caso em que
    /// nao refaz, que deixaria o app sem porta de entrada.
    /// </summary>
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

    /// <summary>Reaplica a cor de destaque. Chamada pela tela de Personalização.</summary>
    public void AplicarAparencia()
    {
        ThemeService.Aplicar(_cfg.Tema);
        AplicarAcento();
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
            // Uma cor invalida ou um registro inacessivel nao pode impedir o app
            // de abrir: fica o azul padrao.
            Log.Erro("Cor de destaque", ex);
        }

        Resources["Acento"] = new SolidColorBrush(cor);
    }

    /// <summary>
    /// Cor de destaque escolhida pelo usuario no Windows. Vem do DWM, em ABGR.
    /// Nao usar SystemParameters.WindowGlassColor: aquilo e a cor do vidro das
    /// janelas, que costuma sair acinzentada e nao acompanha a escolha do usuario.
    /// </summary>
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

            // Um acento quase preto ou quase branco sumiria sobre o painel escuro.
            var brilho = (0.299 * cor.R + 0.587 * cor.G + 0.114 * cor.B) / 255.0;
            return brilho is > 0.12 and < 0.95 ? cor : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Desenha o icone da bandeja com a temperatura da CPU, para o numero ficar
    /// legivel sem abrir o painel.
    /// </summary>
    private void AtualizarBandeja(float? cpuTemp, bool temSensores)
    {
        var texto = temSensores && cpuTemp is > 0 ? $"{cpuTemp:F0}" : "--";
        _bandeja.Text = temSensores
            ? $"Monitor de PC — CPU {texto} °C"
            : "Monitor de PC — sensores de baixo nível indisponíveis";

        // 32 por 32 e desenhado para ser reduzido a 16 na bandeja: quanto mais
        // area o numero ocupar, mais legivel ele fica depois da reducao.
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

            // O tamanho acompanha a quantidade de digitos, para o numero ocupar
            // a largura toda sem estourar a borda.
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

        // O handle do icone anterior precisa ser liberado, senao vaza a cada leitura.
        if (anterior is not null)
        {
            var antigo = anterior.Handle;
            anterior.Dispose();
            DestroyIcon(antigo);
        }
    }

    /// <summary>
    /// Aviso do Windows, para a temperatura alta aparecer mesmo com o painel
    /// fechado. Clicar nele abre o painel.
    /// </summary>
    private void Notificar(Alerta alerta)
    {
        if (!_cfg.NotificarNoWindows) return;

        _bandeja.BalloonTipTitle = alerta.Titulo;
        _bandeja.BalloonTipText = alerta.Detalhe;
        _bandeja.BalloonTipIcon = WF.ToolTipIcon.Warning;
        _bandeja.ShowBalloonTip(8000);
    }

    /// <summary>Quadrado de cantos arredondados, que o GDI+ nao desenha direto.</summary>
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

    /// <summary>
    /// Encerramento completo: o laco de leitura para, o driver e liberado e o
    /// icone sai da bandeja. Cada passo e isolado, para uma falha num deles nao
    /// deixar os outros por fazer.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Tentar(() => _bandeja.Visible = false);
        Tentar(() => _bandeja.Dispose());
        Tentar(() => _sensores.Dispose());
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
