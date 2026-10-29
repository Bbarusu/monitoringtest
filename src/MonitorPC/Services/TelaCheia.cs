using System.Runtime.InteropServices;
using System.Text;

namespace MonitorPC.Services;

/// <summary>
/// Responde se algum programa esta em tela cheia agora, para a ilha sair da
/// frente de jogo e video.
///
/// Sao duas perguntas, porque nenhuma sozinha cobre tudo. O Windows sabe dizer
/// quando um programa Direct3D tomou a tela em modo exclusivo ou quando ha
/// apresentacao em curso — mas jogo em "janela sem borda", que hoje e o
/// padrao, e video em tela cheia no navegador nao entram nessa conta. Para
/// esses, a resposta e geometrica: a janela em primeiro plano cobre o monitor
/// inteiro, barra de tarefas incluida.
/// </summary>
/// <summary>O que esta em tela cheia agora, e onde.</summary>
/// <param name="Exclusiva">
/// Direct3D em modo exclusivo ou apresentacao: o Windows avisa, mas nao diz em
/// qual monitor, e nada consegue aparecer por cima.
/// </param>
/// <param name="Janela">A janela que cobre o monitor, quando e por geometria.</param>
/// <param name="Monitor">O monitor que ela cobre.</param>
public readonly record struct EstadoDeTelaCheia(bool Exclusiva, IntPtr Janela, IntPtr Monitor)
{
    public bool Cheia => Exclusiva || Janela != IntPtr.Zero;
}

public static class TelaCheia
{
    /// <summary>Ha tela cheia em algum lugar. Mantido para quem so quer saber isso.</summary>
    public static bool Agora(IntPtr propria) => Consultar(propria).Cheia;

    /// <summary>
    /// O monitor em que uma janela esta. A Kisland compara com o da tela
    /// cheia: um jogo no outro monitor nao e motivo para ela sumir deste.
    /// </summary>
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

            // "Ocupado" e qualquer app em tela cheia: sem janela que a geometria
            // confirme, vale como exclusiva, que e o lado seguro.
            var ocupado = estado == EstadoOcupado;

            if (frente == IntPtr.Zero || frente == propria)
                return new EstadoDeTelaCheia(ocupado, IntPtr.Zero, IntPtr.Zero);

            // A area de trabalho e a barra de tarefas tambem cobrem o monitor
            // inteiro, e nao sao tela cheia de ninguem.
            var classe = Classe(frente);
            if (classe is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return default;

            // Janela maximizada comum, com barra de titulo, nao e tela cheia —
            // e com a barra de tarefas em ocultar automaticamente ela cobriria
            // o monitor inteiro e enganaria a conta abaixo.
            if (IsZoomed(frente) && (GetWindowLong(frente, EstiloDaJanela) & ComBarraDeTitulo) == ComBarraDeTitulo)
                return default;

            if (CobreOMonitor(frente, out var monitor))
                return new EstadoDeTelaCheia(false, frente, monitor);

            return new EstadoDeTelaCheia(ocupado, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Na duvida a ilha fica: sumir sem motivo seria pior.
            return default;
        }
    }

    /// <summary>A janela cobre o monitor inteiro em que esta, barra de tarefas incluida.</summary>
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

    /// <summary>Quanto do monitor a janela ocupa, de 0 a 1.</summary>
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

    // ------------------------------------------------------------------

    private const int EstadoOcupado = 2;
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
}
