using System.Runtime.InteropServices;
using System.Text;

namespace MonitorPC.Services;

public readonly record struct EstadoDeTelaCheia(bool Exclusiva, IntPtr Janela, IntPtr Monitor)
{
    public bool Cheia => Exclusiva || Janela != IntPtr.Zero;
}

public static class TelaCheia
{
    public static bool Agora(IntPtr propria) => Consultar(propria).Cheia;

    public static IntPtr MonitorDe(IntPtr janela) =>
        janela == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(janela, MonitorMaisProximo);

    public static EstadoDeTelaCheia Consultar(IntPtr propria)
    {
        try
        {
            if (SHQueryUserNotificationState(out var estado) == 0 &&
                estado is EstadoD3DTelaCheia or EstadoApresentacao)
                return new EstadoDeTelaCheia(true, IntPtr.Zero, IntPtr.Zero);

            var frente = GetForegroundWindow();

            var ocupado = estado == EstadoOcupado;

            if (frente == IntPtr.Zero || frente == propria)
                return ocupado ? ProcurarTelaCheia(propria) : default;

            var classe = Classe(frente);
            if (classe is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return default;

            if (IsZoomed(frente) && (GetWindowLong(frente, EstiloDaJanela) & ComBarraDeTitulo) == ComBarraDeTitulo)
                return default;

            if (CobreOMonitor(frente, out var monitor))
                return new EstadoDeTelaCheia(false, frente, monitor);

            return ocupado ? ProcurarTelaCheia(propria) : default;
        }
        catch
        {
            return default;
        }
    }

    private static EstadoDeTelaCheia ProcurarTelaCheia(IntPtr propria)
    {
        var achada = new EstadoDeTelaCheia(false, IntPtr.Zero, IntPtr.Zero);
        EnumWindows((janela, _) =>
        {
            if (janela == propria || !IsWindowVisible(janela) || IsIconic(janela) || Escondida(janela)) return true;

            var classe = Classe(janela);
            if (classe is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (IsZoomed(janela) && (GetWindowLong(janela, EstiloDaJanela) & ComBarraDeTitulo) == ComBarraDeTitulo) return true;

            if (!CobreOMonitor(janela, out var monitor)) return true;

            achada = new EstadoDeTelaCheia(false, janela, monitor);
            return false;
        }, IntPtr.Zero);

        return achada;
    }

    private static bool Escondida(IntPtr janela) =>
        DwmGetWindowAttribute(janela, Encoberta, out var valor, sizeof(int)) == 0 && valor != 0;

    public static string Descrever(IntPtr propria, EstadoDeTelaCheia estado)
    {
        try
        {
            SHQueryUserNotificationState(out var aviso);
            var frente = GetForegroundWindow();
            var quem = estado.Janela != IntPtr.Zero ? estado.Janela : frente;
            GetWindowRect(quem, out var r);
            return $"aviso do Windows {aviso}, exclusiva {estado.Exclusiva}, " +
                   $"janela {Classe(quem)} de {Processo(quem)} ({r.Esquerda},{r.Topo})-({r.Direita},{r.Baixo})" +
                   (quem != frente ? $", na frente {Classe(frente)} de {Processo(frente)}" : "");
        }
        catch (Exception ex)
        {
            return "sem detalhes: " + ex.Message;
        }
    }

    private static string Processo(IntPtr janela)
    {
        try
        {
            GetWindowThreadProcessId(janela, out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    public static bool CobreOMonitor(IntPtr janela, out IntPtr monitor)
    {
        monitor = IntPtr.Zero;
        if (!GetWindowRect(janela, out var r)) return false;

        monitor = MonitorFromWindow(janela, MonitorMaisProximo);
        var info = new InfoDoMonitor { Tamanho = Marshal.SizeOf<InfoDoMonitor>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        var area = info.Area;
        return r.Esquerda <= area.Esquerda && r.Topo <= area.Topo &&
               r.Direita >= area.Direita && r.Baixo >= area.Baixo;
    }

    public static double FracaoDoMonitor(IntPtr janela)
    {
        if (!GetWindowRect(janela, out var r)) return 0;

        var monitor = MonitorFromWindow(janela, MonitorMaisProximo);
        var info = new InfoDoMonitor { Tamanho = Marshal.SizeOf<InfoDoMonitor>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return 0;

        var a = info.Area;
        double areaMonitor = (double)(a.Direita - a.Esquerda) * (a.Baixo - a.Topo);
        double areaJanela = (double)Math.Max(0, r.Direita - r.Esquerda) * Math.Max(0, r.Baixo - r.Topo);
        return areaMonitor <= 0 ? 0 : Math.Min(1, areaJanela / areaMonitor);
    }

    private static string Classe(IntPtr janela)
    {
        var texto = new StringBuilder(64);
        return GetClassName(janela, texto, texto.Capacity) > 0 ? texto.ToString() : "";
    }

    private const int EstadoOcupado = 2;
    private const int Encoberta = 14;
    private const int EstadoD3DTelaCheia = 3;
    private const int EstadoApresentacao = 4;
    private const int MonitorMaisProximo = 2;
    private const int EstiloDaJanela = -16;
    private const int ComBarraDeTitulo = 0x00C00000;

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

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int estado);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr janela);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr janela, int indice);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr janela, StringBuilder classe, int tamanho);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr janela, out Retangulo retangulo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr janela, int padrao);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref InfoDoMonitor info);

    private delegate bool CadaJanela(IntPtr janela, IntPtr dado);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(CadaJanela funcao, IntPtr dado);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr janela);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr janela);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr janela, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr janela, int atributo, out int valor, int tamanho);
}
