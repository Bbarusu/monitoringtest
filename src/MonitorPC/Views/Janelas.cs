using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MonitorPC.Views;

public static class Janelas
{
    private static readonly HashSet<Window> EmCurso = new();

    public static void Acompanhar(Window janela)
    {
        PermitirRolagem(janela);
        janela.Loaded += (_, _) => Ancorar(janela, janela.Owner);
        janela.SizeChanged += (_, _) => Ancorar(janela, janela.Owner);
    }

    public static void ArrastarPeloFundo(Window janela, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;

        for (var origem = e.OriginalSource as DependencyObject; origem is Visual; origem = VisualTreeHelper.GetParent(origem))
            if (origem is System.Windows.Controls.Primitives.ButtonBase) return;

        try { janela.DragMove(); }
        catch (InvalidOperationException) { }
    }

    public static void CaberNaTela(Window janela)
    {
        PermitirRolagem(janela);

        void Ajustar()
        {
            if (!EmCurso.Add(janela)) return;
            try
            {
                LimitarAltura(janela);
                Conter(janela);
            }
            finally
            {
                EmCurso.Remove(janela);
            }
        }

        janela.Loaded += (_, _) => Ajustar();
        janela.SizeChanged += (_, _) => Ajustar();
        janela.DpiChanged += (_, _) => Ajustar();
    }

    public static void PermitirRolagem(Window janela)
    {
        if (janela.Content is not Border painel) return;
        if (painel.Tag is "RolagemPropria") return;
        if (painel.Child is not FrameworkElement conteudo || conteudo is ScrollViewer) return;

        var folga = Math.Min(14, painel.Padding.Right);

        painel.Child = null;
        var m = conteudo.Margin;
        conteudo.Margin = new Thickness(m.Left, m.Top, m.Right + folga, m.Bottom);

        painel.Child = new ScrollViewer
        {
            Content = conteudo,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, -folga, 0),
            Focusable = false
        };
    }

    public static void Ancorar(Window janela, Window? dono)
    {
        if (!EmCurso.Add(janela)) return;

        try
        {
            janela.UpdateLayout();

            LimitarAltura(janela);

            if (janela.ActualHeight <= 0) return;

            double direita, baixo;

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

    public static void LimitarAltura(Window janela)
    {
        var teto = AreaDoMonitor(janela).Height;

        if (teto < 240) return;

        if (Math.Abs(janela.MaxHeight - teto) < 0.5) return;

        janela.MaxHeight = teto;
        janela.UpdateLayout();
    }

    public static void Conter(Window janela)
    {
        if (janela.ActualWidth <= 0 || janela.ActualHeight <= 0) return;

        var area = AreaDoMonitor(janela);

        janela.Left = Math.Clamp(janela.Left, area.Left,
            Math.Max(area.Left, area.Right - janela.ActualWidth));

        janela.Top = Math.Clamp(janela.Top, area.Top,
            Math.Max(area.Top, area.Bottom - janela.ActualHeight));
    }

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
