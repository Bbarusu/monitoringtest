using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MonitorPC.Views;

/// <summary>
/// Posicionamento comum das janelas. O painel pode ter sido arrastado para
/// qualquer canto de qualquer monitor, e as telas que ele abre precisam
/// aparecer perto dele.
/// </summary>
public static class Janelas
{
    /// <summary>
    /// Janelas em que o reposicionamento esta em curso. Ancorar chama
    /// UpdateLayout, que dispara SizeChanged, que chamaria Ancorar de novo.
    /// </summary>
    private static readonly HashSet<Window> EmCurso = new();

    /// <summary>
    /// Faz a janela seguir o dono enquanto existir.
    ///
    /// A ancoragem precisa correr depois do layout: com SizeToContent, a altura
    /// ainda e zero quando a janela ganha o handle, e ancorar pela base colocaria
    /// o topo no fim da tela. Por isso Loaded e SizeChanged, nao SourceInitialized.
    /// </summary>
    public static void Acompanhar(Window janela)
    {
        janela.Loaded += (_, _) => Ancorar(janela, janela.Owner);
        janela.SizeChanged += (_, _) => Ancorar(janela, janela.Owner);
    }

    /// <summary>
    /// Alinha pela base e pela direita do dono; sem dono, pelo canto inferior
    /// direito do monitor principal. Sempre dentro do monitor em que estiver.
    /// </summary>
    public static void Ancorar(Window janela, Window? dono)
    {
        if (!EmCurso.Add(janela)) return;

        try
        {
            janela.UpdateLayout();

            LimitarAltura(janela);

            // Sem altura ainda, qualquer conta pela base erra: espera o layout.
            if (janela.ActualHeight <= 0) return;

            double direita, baixo;

            // Nao exige que o dono esteja visivel: uma janela escondida mantem
            // posicao e tamanho, e e por eles que a tela deve se guiar.
            if (dono is { ActualWidth: > 0, ActualHeight: > 0 } &&
                !double.IsNaN(dono.Left) && !double.IsNaN(dono.Top))
            {
                direita = dono.Left + dono.ActualWidth;
                baixo = dono.Top + dono.ActualHeight;
            }
            else
            {
                var principal = SystemParameters.WorkArea;
                direita = principal.Right;
                baixo = principal.Bottom;
            }

            janela.Left = direita - janela.ActualWidth;
            janela.Top = baixo - janela.ActualHeight;

            Conter(janela);
        }
        finally
        {
            EmCurso.Remove(janela);
        }
    }

    /// <summary>
    /// Impede a janela de passar da altura util do monitor.
    ///
    /// Todas as janelas do app usam <c>SizeToContent="Height"</c>: elas crescem
    /// com o conteudo, o que e certo numa tela grande e vira defeito numa
    /// pequena. Num notebook de tela baixa a aba Disco passava do fim da tela, e
    /// <see cref="Conter"/> nao resolvia porque ele reposiciona e nunca encurta
    /// — encostado no topo, o excedente continuava fora do alcance.
    ///
    /// E o teto daqui que faz a rolagem entrar em acao: sem ele, a janela pede
    /// a altura inteira do conteudo, o ScrollViewer recebe tudo que pediu e
    /// nunca mostra barra.
    ///
    /// Fica aqui, e nao na janela, porque e aqui que mora o conhecimento de qual
    /// monitor e qual a area util dele — e porque vale para todas as janelas, e
    /// nao so para o painel que levantou o problema.
    /// </summary>
    public static void LimitarAltura(Window janela)
    {
        // AreaDoMonitor ja devolve em unidades do WPF, convertidas pela escala
        // do monitor. Misturar com pixel fisico aqui faria o teto nao valer
        // nada numa tela com escala de 150%.
        var teto = AreaDoMonitor(janela).Height;

        // Um piso evita que uma leitura estranha da area util deixe a janela
        // inutilizavel.
        if (teto < 240) return;

        // Reatribuir a cada ancoragem dispararia layout a cada leitura de
        // sensor, porque SizeChanged chama Ancorar.
        if (Math.Abs(janela.MaxHeight - teto) < 0.5) return;

        janela.MaxHeight = teto;
        janela.UpdateLayout();
    }

    /// <summary>
    /// Puxa a janela de volta quando ela passaria da area util — do monitor em
    /// que ela esta, nao do principal.
    /// </summary>
    public static void Conter(Window janela)
    {
        if (janela.ActualWidth <= 0 || janela.ActualHeight <= 0) return;

        var area = AreaDoMonitor(janela);

        janela.Left = Math.Clamp(janela.Left, area.Left,
            Math.Max(area.Left, area.Right - janela.ActualWidth));

        janela.Top = Math.Clamp(janela.Top, area.Top,
            Math.Max(area.Top, area.Bottom - janela.ActualHeight));
    }

    /// <summary>
    /// Area util do monitor onde a janela esta.
    ///
    /// SystemParameters.WorkArea so conhece o monitor principal: usar aquilo
    /// empurrava de volta para o principal tudo que estivesse no secundario.
    /// </summary>
    public static Rect AreaDoMonitor(Window janela)
    {
        try
        {
            var handle = new WindowInteropHelper(janela).Handle;
            if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

            var monitor = MonitorFromWindow(handle, MonitorMaisProximo);
            if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

            var info = new InfoDoMonitor { Tamanho = Marshal.SizeOf<InfoDoMonitor>() };
            if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

            // A API devolve pixels; as posicoes do WPF sao em unidades que
            // acompanham a escala do monitor.
            var escala = VisualTreeHelper.GetDpi(janela);
            var x = escala.DpiScaleX <= 0 ? 1 : escala.DpiScaleX;
            var y = escala.DpiScaleY <= 0 ? 1 : escala.DpiScaleY;

            return new Rect(
                info.AreaUtil.Esquerda / x,
                info.AreaUtil.Topo / y,
                (info.AreaUtil.Direita - info.AreaUtil.Esquerda) / x,
                (info.AreaUtil.Baixo - info.AreaUtil.Topo) / y);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    // ------------------------------------------------------------------

    private const int MonitorMaisProximo = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Retangulo
    {
        public int Esquerda;
        public int Topo;
        public int Direita;
        public int Baixo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoDoMonitor
    {
        public int Tamanho;
        public Retangulo Area;
        public Retangulo AreaUtil;
        public uint Sinalizadores;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr janela, int padrao);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref InfoDoMonitor info);
}
