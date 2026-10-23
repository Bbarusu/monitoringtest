using System.Windows;
using System.Windows.Media;

namespace MonitorPC.Services;

public enum Tema
{
    Escuro,
    Claro,
    Sistema
}

/// <summary>
/// Troca a paleta do app em tempo de execucao. As telas referenciam estas
/// chaves por DynamicResource, entao mudar aqui repinta tudo sem reabrir.
/// </summary>
public static class ThemeService
{
    private static readonly (string Chave, string Escuro, string Claro)[] Paleta =
    {
        ("Fundo",           "#15171C", "#FFFFFF"),
        ("Borda",           "#262A32", "#E3E6EB"),
        ("Cartao",          "#1A1D23", "#F5F6F8"),
        ("Chip",            "#1D2026", "#EFF1F4"),
        ("Trilho",          "#2A2E36", "#DDE1E7"),
        ("Texto",           "#E6E9EF", "#15171C"),
        ("TextoFraco",      "#9AA3B2", "#5B6370"),
        ("TextoMuitoFraco", "#8B94A3", "#767E8B"),
        ("Alerta",          "#F97316", "#D9600B"),
        ("AlertaTexto",     "#FDBA74", "#9A4B06")
    };

    /// <summary>Cor de fundo em vigor, para quem precisa dela com alpha.</summary>
    public static Color CorDeFundo { get; private set; } = (Color)ColorConverter.ConvertFromString("#15171C")!;

    public static bool Claro { get; private set; }

    /// <summary>
    /// Pincel da paleta em vigor. Elementos criados em codigo ficam com a cor do
    /// momento; os que precisam acompanhar a troca de tema usam
    /// SetResourceReference com a mesma chave.
    /// </summary>
    public static Brush Pincel(string chave) => (Brush)Application.Current.Resources[chave];

    public static void Aplicar(Tema tema)
    {
        Claro = tema switch
        {
            Tema.Claro => true,
            Tema.Escuro => false,
            _ => SistemaEstaClaro()
        };

        var recursos = Application.Current.Resources;

        foreach (var (chave, escuro, claro) in Paleta)
        {
            var cor = (Color)ColorConverter.ConvertFromString(Claro ? claro : escuro)!;
            recursos[chave] = new SolidColorBrush(cor);

            if (chave == "Fundo") CorDeFundo = cor;
        }
    }

    /// <summary>O Windows guarda isto como "apps usam tema claro".</summary>
    private static bool SistemaEstaClaro()
    {
        try
        {
            using var chave = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return chave?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
