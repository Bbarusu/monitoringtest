using System.Windows;
using System.Windows.Media;

namespace MonitorPC.Setup;

public partial class App : Application
{
    /// <summary>
    /// Passado pelo atalho de desinstalacao e pelo registro do Windows. Sem
    /// argumento, o instalador instala.
    /// </summary>
    public const string ArgumentoDeDesinstalacao = "--desinstalar";

    /// <summary>
    /// Passado pelo proprio app quando ele baixa uma versao nova. Com uma
    /// instalacao anterior encontrada, o instalador vira o painel de
    /// atualizacao: sem opcoes a escolher de novo, ja comecando.
    ///
    /// Rodado a mao, o instalador continua mostrando as opcoes, que e o jeito
    /// de mudar alguma delas.
    /// </summary>
    public const string ArgumentoDeAtualizacao = "--atualizar";

    public static bool Desinstalando => Tem(ArgumentoDeDesinstalacao);
    public static bool Atualizando => Tem(ArgumentoDeAtualizacao);

    private static bool Tem(string argumento) =>
        Environment.GetCommandLineArgs()
            .Any(a => a.Equals(argumento, StringComparison.OrdinalIgnoreCase));

    /// <summary>Valor de um argumento no formato --nome=valor.</summary>
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

    /// <summary>
    /// O app manda a cor de destaque e o tema que estao em uso nele, e o
    /// instalador se veste igual: atualizar nao deve parecer abrir outro
    /// programa.
    ///
    /// Vem pela linha de comando, e nao pela leitura do config.json: o
    /// instalador roda elevado, e quando o Windows pede a senha de outra conta
    /// de administrador a pasta de dados que ele enxerga e a dessa outra conta.
    /// Sem argumento, fica a paleta escura e o azul de sempre.
    /// </summary>
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
                // Cor ilegivel: fica o azul padrao.
            }
        }
    }

    /// <summary>Os mesmos valores do tema claro do app, para as chaves que o instalador usa.</summary>
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
