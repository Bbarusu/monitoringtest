using System.Windows;
using System.Windows.Media;

namespace MonitorPC.Services;

public enum Tema
{
    Escuro,
    Claro,
    Sistema,

    Preto
}

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
        ("AlertaTexto",     "#FDBA74", "#9A4B06"),

        ("Icone",           "#C4CAD6", "#5B6370"),
        ("IconeFraco",      "#8B94A3", "#767E8B"),

        ("AvisoFundo",      "#2A1A10", "#FEF2E4"),
        ("AvisoBorda",      "#7C3A14", "#F3C79A"),
        ("AvisoIcone",      "#FB923C", "#C2600A"),
        ("AvisoChip",       "#3A2113", "#FAE0C6"),
        ("AvisoBotao",      "#F97316", "#C2600A"),
        ("AvisoBotaoTexto", "#1A0F08", "#FFFFFF"),

        ("SerieMedida",     "#8FBAFF", "#2563EB"),
        ("SerieEstimada",   "#4B5365", "#A9B0BD"),
        ("InfoFundo",       "#16233D", "#E4EDFF"),
        ("InfoTexto",       "#8FBAFF", "#1D4ED8")
    };

    private static readonly Dictionary<string, string> Preta = new()
    {
        ["Fundo"] = "#000000",
        ["Borda"] = "#16181C",
        ["Cartao"] = "#0A0A0B",
        ["Chip"] = "#111214",
        ["Trilho"] = "#1C1E22"
    };

    public static Dictionary<string, Brush> PaletaPreta()
    {
        var pinceis = new Dictionary<string, Brush>();
        foreach (var (chave, escuro, _) in Paleta)
        {
            var pincel = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                Preta.TryGetValue(chave, out var preta) ? preta : escuro)!);
            pincel.Freeze();
            pinceis[chave] = pincel;
        }

        return pinceis;
    }

    public static bool PretoTotal { get; private set; }

    public static Color CorDeFundo { get; private set; } = (Color)ColorConverter.ConvertFromString("#15171C")!;

    public static bool Claro { get; private set; }

    public static Brush Pincel(string chave) => (Brush)Application.Current.Resources[chave];

    public static void Aplicar(Tema tema)
    {
        Claro = tema switch
        {
            Tema.Claro => true,
            Tema.Escuro or Tema.Preto => false,
            _ => SistemaEstaClaro()
        };
        PretoTotal = tema == Tema.Preto;

        var recursos = Application.Current.Resources;

        foreach (var (chave, escuro, claro) in Paleta)
        {
            var hex = Claro ? claro
                    : PretoTotal && Preta.TryGetValue(chave, out var preta) ? preta
                    : escuro;
            var cor = (Color)ColorConverter.ConvertFromString(hex)!;
            recursos[chave] = new SolidColorBrush(cor);

            if (chave == "Fundo") CorDeFundo = cor;
        }
    }

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
