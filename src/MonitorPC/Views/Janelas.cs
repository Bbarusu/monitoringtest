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
    /// Alinha pela base e pela direita do dono; sem dono, pelo canto inferior
    /// direito da area util. Sempre dentro da tela.
    /// </summary>
    public static void Ancorar(Window janela, Window? dono)
    {
        janela.UpdateLayout();

        var area = SystemParameters.WorkArea;
        double direita, baixo;

        if (dono is { IsVisible: true })
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

    /// <summary>Puxa a janela de volta quando ela passaria da area util.</summary>
    public static void Conter(Window janela, Rect area)
    {
        if (janela.ActualWidth <= 0 || janela.ActualHeight <= 0) return;

        janela.Left = Math.Clamp(janela.Left, area.Left, Math.Max(area.Left, area.Right - janela.ActualWidth));
        janela.Top = Math.Clamp(janela.Top, area.Top, Math.Max(area.Top, area.Bottom - janela.ActualHeight));
    }
}
