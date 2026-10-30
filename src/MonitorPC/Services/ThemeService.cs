using System.Windows;
using System.Windows.Media;

namespace MonitorPC.Services;

public enum Tema
{
    Escuro,
    Claro,
    Sistema,

    /// <summary>
    /// O escuro com preto de verdade no fundo, para telas AMOLED: pixel preto
    /// e pixel apagado, que gasta zero e da o contraste maximo. No fim da
    /// lista para nao mudar o numero dos outros na configuracao salva.
    /// </summary>
    Preto
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
        ("AlertaTexto",     "#FDBA74", "#9A4B06"),

        // Tracos de icone. No escuro sao um cinza claro; no claro precisam
        // escurecer, senao os contornos somem no branco do fundo.
        ("Icone",           "#C4CAD6", "#5B6370"),
        ("IconeFraco",      "#8B94A3", "#767E8B"),

        // Cartoes de aviso. O laranja escuro de fundo viraria uma mancha preta
        // no tema claro, entao a versao clara inverte: fundo creme, traco forte.
        ("AvisoFundo",      "#2A1A10", "#FEF2E4"),
        ("AvisoBorda",      "#7C3A14", "#F3C79A"),
        ("AvisoIcone",      "#FB923C", "#C2600A"),
        ("AvisoChip",       "#3A2113", "#FAE0C6"),
        ("AvisoBotao",      "#F97316", "#C2600A"),
        ("AvisoBotaoTexto", "#1A0F08", "#FFFFFF"),

        // Series dos graficos e legendas: o que foi medido e o que foi estimado.
        ("SerieMedida",     "#8FBAFF", "#2563EB"),
        ("SerieEstimada",   "#4B5365", "#A9B0BD"),
        ("InfoFundo",       "#16233D", "#E4EDFF"),
        ("InfoTexto",       "#8FBAFF", "#1D4ED8")
    };

    /// <summary>
    /// O que o tema Preto troca no escuro: so as superficies. Texto, icones e
    /// avisos ficam os do escuro. Tambem e a paleta da Kisland preta.
    /// </summary>
    private static readonly Dictionary<string, string> Preta = new()
    {
        ["Fundo"] = "#000000",
        ["Borda"] = "#16181C",
        ["Cartao"] = "#0A0A0B",
        ["Chip"] = "#111214",
        ["Trilho"] = "#1C1E22"
    };

    /// <summary>
    /// A paleta escura com as superficies pretas, pronta para pendurar nos
    /// recursos de uma janela so (a Kisland preta), sem mexer no resto do app.
    /// </summary>
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

    /// <summary>Tema Preto em vigor: sem transparencia, que acabaria com o preto.</summary>
    public static bool PretoTotal { get; private set; }

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
