using System.Runtime.InteropServices;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstalarCapturaDeErros();

        _cfg = AppConfig.Carregar();
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
        };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => _painel.Mostrar());
        menu.Items.Add("Ajustes", null, (_, _) =>
        {
            _painel.Mostrar();
            _painel.AbrirConfiguracao(false);
        });
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Shutdown());
        _bandeja.ContextMenuStrip = menu;
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
    /// <summary>Reaplica a cor de destaque. Chamada pela tela de Personalização.</summary>
    public void AplicarAparencia() => AplicarAcento();

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

        using var bmp = new D.Bitmap(32, 32, D.Imaging.PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = D.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = D.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(D.Color.Transparent);

            var acento = ((SolidColorBrush)Resources["Acento"]).Color;
            using var fundo = new D.SolidBrush(D.Color.FromArgb(230, 21, 23, 28));
            using var contorno = new D.Pen(
                D.Color.FromArgb(temSensores ? 255 : 90, acento.R, acento.G, acento.B), 2.5f);

            g.FillEllipse(fundo, 1, 1, 30, 30);
            g.DrawEllipse(contorno, 2, 2, 28, 28);

            using var fonte = new D.Font("Segoe UI", texto.Length > 2 ? 11f : 13f, D.FontStyle.Bold);
            using var branco = new D.SolidBrush(D.Color.White);
            var tam = g.MeasureString(texto, fonte);
            g.DrawString(texto, fonte, branco, (32 - tam.Width) / 2, (32 - tam.Height) / 2);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void OnExit(ExitEventArgs e)
    {
        _bandeja.Visible = false;
        _bandeja.Dispose();
        _sensores.Dispose();
        _cfg.Salvar();
        base.OnExit(e);
    }
}
