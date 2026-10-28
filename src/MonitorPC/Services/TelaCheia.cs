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
public static class TelaCheia
{
    public static bool Agora(IntPtr propria)
    {
        try
        {
            if (SHQueryUserNotificationState(out var estado) == 0 &&
                estado is EstadoOcupado or EstadoD3DTelaCheia or EstadoApresentacao)
                return true;

            var frente = GetForegroundWindow();
            if (frente == IntPtr.Zero || frente == propria) return false;

            // A area de trabalho e a barra de tarefas tambem cobrem o monitor
            // inteiro, e nao sao tela cheia de ninguem.
            var classe = Classe(frente);
            if (classe is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return false;

            // Janela maximizada comum, com barra de titulo, nao e tela cheia —
            // e com a barra de tarefas em ocultar automaticamente ela cobriria
            // o monitor inteiro e enganaria a conta abaixo.
            if (IsZoomed(frente) && (GetWindowLong(frente, EstiloDaJanela) & ComBarraDeTitulo) == ComBarraDeTitulo)
                return false;

            if (!GetWindowRect(frente, out var janela)) return false;

            var monitor = MonitorFromWindow(frente, MonitorMaisProximo);
            var info = new InfoDoMonitor { Tamanho = Marshal.SizeOf<InfoDoMonitor>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

            var area = info.Area;
            return janela.Esquerda <= area.Esquerda && janela.Topo <= area.Topo &&
                   janela.Direita >= area.Direita && janela.Baixo >= area.Baixo;
        }
        catch
        {
            // Na duvida a ilha fica: sumir sem motivo seria pior.
            return false;
        }
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
