using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Media;
using MonitorPC.Services;
using MonitorPC.Views;
using Application = System.Windows.Application;

namespace MonitorPC;

public partial class App : Application
{
    private AppConfig _cfg = null!;
    private SensorService _sensores = null!;
    private System.Windows.Forms.NotifyIcon _bandeja = null!;
    private FlyoutWindow _painel = null!;
    private Icon? _iconeAtual;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _cfg = AppConfig.Carregar();
        AplicarAcento();

        _sensores = new SensorService(_cfg);
        _painel = new FlyoutWindow(_cfg, _sensores);

        _bandeja = new System.Windows.Forms.NotifyIcon
        {
            Text = "Monitor de PC",
            Visible = true,
            Icon = SystemIcons.Application
        };
        _bandeja.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
                _painel.Alternar();
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => _painel.Mostrar());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Shutdown());
        _bandeja.ContextMenuStrip = menu;

        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() =>
        {
            AtualizarBandeja(snap.CpuTemp, snap.SensoresDeBaixoNivel);
        });

        _sensores.Iniciar();
    }

    /// <summary>
    /// O acento acompanha a cor do Windows quando a configuracao pede;
    /// senao usa a cor escolhida pelo usuario.
    /// </summary>
    private void AplicarAcento()
    {
        var cor = (Color)ColorConverter.ConvertFromString(_cfg.CorDestaque)!;

        if (_cfg.AcompanharCorDoSistema)
        {
            try
            {
                var sistema = SystemParameters.WindowGlassColor;
                if (sistema.A > 0) cor = sistema;
            }
            catch
            {
                // Sem cor do sistema disponivel, fica a configurada.
            }
        }

        Resources["Acento"] = new SolidColorBrush(cor);
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

        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(System.Drawing.Color.Transparent);

            var acento = ((SolidColorBrush)Resources["Acento"]).Color;
            using var fundo = new SolidBrush(System.Drawing.Color.FromArgb(230, 21, 23, 28));
            using var contorno = new Pen(System.Drawing.Color.FromArgb(
                temSensores ? 255 : 90, acento.R, acento.G, acento.B), 2.5f);

            g.FillEllipse(fundo, 1, 1, 30, 30);
            g.DrawEllipse(contorno, 2, 2, 28, 28);

            using var fonte = new Font("Segoe UI", texto.Length > 2 ? 11f : 13f, System.Drawing.FontStyle.Bold);
            using var branco = new SolidBrush(System.Drawing.Color.White);
            var tam = g.MeasureString(texto, fonte);
            g.DrawString(texto, fonte, branco, (32 - tam.Width) / 2, (32 - tam.Height) / 2);
        }

        var anterior = _iconeAtual;
        _iconeAtual = Icon.FromHandle(bmp.GetHicon());
        _bandeja.Icon = _iconeAtual;

        // O handle do icone anterior precisa ser liberado, senao vaza a cada leitura.
        if (anterior is not null)
        {
            DestroyIcon(anterior.Handle);
            anterior.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
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
