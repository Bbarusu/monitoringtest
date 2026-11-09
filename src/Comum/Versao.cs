using System.Reflection;

namespace MonitorPC.Comum;

public static class Versao
{
    public static string Texto { get; } = Ler();

    public static string Completa { get; } = LerCompleta();

    public static Version? Numero { get; } =
        Version.TryParse(Texto, out var v) ? v : null;

    private static string LerCompleta()
    {
        try
        {
            var bruto = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            return string.IsNullOrWhiteSpace(bruto) ? Texto : bruto;
        }
        catch
        {
            return Texto;
        }
    }

    private static string Ler()
    {
        try
        {
            var bruto = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(bruto))
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

            var corte = bruto.IndexOf('+');
            return corte > 0 ? bruto[..corte] : bruto;
        }
        catch
        {
            return "0.0.0";
        }
    }

    public static bool EhMaisNova(string? candidata) =>
        Numero is { } atual
        && Version.TryParse(candidata, out var nova)
        && nova > atual;
}
