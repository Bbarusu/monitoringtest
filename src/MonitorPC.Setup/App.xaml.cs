using System.Windows;
using System.Windows.Media;

namespace MonitorPC.Setup;

public partial class App : Application
{
    public const string ArgumentoDeDesinstalacao = "--desinstalar";

    public const string ArgumentoDeAtualizacao = "--atualizar";

    public static bool Desinstalando => Tem(ArgumentoDeDesinstalacao);
    public static bool Atualizando => Tem(ArgumentoDeAtualizacao);

    private static bool Tem(string argumento) =>
        Environment.GetCommandLineArgs()
            .Any(a => a.Equals(argumento, StringComparison.OrdinalIgnoreCase));

    private static string? Valor(string nome)
    {
        var prefixo = nome + "=";
        return Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase))?[prefixo.Length..];
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AplicarAparenciaDoApp();
        base.OnStartup(e);
    }

    private void AplicarAparenciaDoApp()
    {
        if (string.Equals(Valor("--tema"), "claro", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (chave, hex) in PaletaClara)
                Resources[chave] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }

        if (Valor("--acento") is { Length: > 0 } acento)
        {
            try
            {
                Resources["Acento"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(acento)!);
            }
            catch
            {
            }
        }
    }

    private static readonly (string Chave, string Hex)[] PaletaClara =
    {
        ("Fundo",           "#FFFFFF"),
        ("Borda",           "#E3E6EB"),
        ("Cartao",          "#F5F6F8"),
        ("Chip",            "#EFF1F4"),
        ("Trilho",          "#DDE1E7"),
        ("Texto",           "#15171C"),
        ("TextoFraco",      "#5B6370"),
        ("TextoMuitoFraco", "#767E8B"),
        ("Icone",           "#5B6370"),
        ("AvisoFundo",      "#FEF2E4"),
        ("AvisoBorda",      "#F3C79A"),
        ("AvisoIcone",      "#C2600A"),
        ("AlertaTexto",     "#9A4B06"),
        ("Sucesso",         "#15803D")
    };
}
