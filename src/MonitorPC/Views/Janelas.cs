using System.Windows;

namespace MonitorPC.Views;

/// <summary>
/// Posicionamento comum das janelas. O painel pode ter sido arrastado para
/// qualquer canto, e as telas que ele abre precisam aparecer perto dele, nao
/// no canto da tela.
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
    /// direito da area util. Sempre dentro da tela.
    /// </summary>
    public static void Ancorar(Window janela, Window? dono)
    {
        if (!EmCurso.Add(janela)) return;

        try
        {
            janela.UpdateLayout();

            // Sem altura ainda, qualquer conta pela base erra: espera o layout.
            if (janela.ActualHeight <= 0) return;

            var area = SystemParameters.WorkArea;
            double direita, baixo;

            if (dono is { IsVisible: true, ActualHeight: > 0 })
            {
                direita = dono.Left + dono.ActualWidth;
                baixo = dono.Top + dono.ActualHeight;
            }
            else
            {
                direita = area.Right;
                baixo = area.Bottom;
            }

            janela.Left = direita - janela.ActualWidth;
            janela.Top = baixo - janela.ActualHeight;

            Conter(janela, area);
        }
        finally
        {
            EmCurso.Remove(janela);
        }
    }

    /// <summary>Puxa a janela de volta quando ela passaria da area util.</summary>
    public static void Conter(Window janela, Rect area)
    {
        if (janela.ActualWidth <= 0 || janela.ActualHeight <= 0) return;

        janela.Left = Math.Clamp(janela.Left, area.Left,
            Math.Max(area.Left, area.Right - janela.ActualWidth));

        janela.Top = Math.Clamp(janela.Top, area.Top,
            Math.Max(area.Top, area.Bottom - janela.ActualHeight));
    }
}
