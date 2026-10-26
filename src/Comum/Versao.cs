using System.Reflection;

namespace MonitorPC.Comum;

/// <summary>
/// A versao do que esta rodando, lida do proprio executavel.
///
/// Este arquivo e compilado nos dois projetos — app e instalador — pelo mesmo
/// caminho, de proposito. Os dois precisam concordar sobre o que e "a versao
/// atual": se discordarem, o app oferece uma atualizacao que ja tem, ou nunca
/// oferece a que falta. Duplicar seis linhas seria mais barato de escrever e
/// mais caro de manter certo.
///
/// O numero vem do Directory.Build.props na raiz, que e a unica fonte.
/// </summary>
public static class Versao
{
    /// <summary>Ex.: "0.1.0". Nunca lanca: versao e informacao, nao operacao.</summary>
    public static string Texto { get; } = Ler();

    /// <summary>
    /// A mesma versao em forma comparavel. <c>null</c> quando nao da para
    /// interpretar — e ai quem compara deve desistir em vez de chutar.
    /// </summary>
    public static Version? Numero { get; } =
        Version.TryParse(Texto, out var v) ? v : null;

    private static string Ler()
    {
        try
        {
            // InformationalVersion e o que carrega "0.1.0" como escrito. A
            // AssemblyVersion viraria "0.1.0.0", e comparar as duas formas daria
            // diferenca onde nao ha.
            var bruto = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(bruto))
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

            // O SDK pode acrescentar "+<hash do commit>"; o que interessa esta
            // antes do sinal.
            var corte = bruto.IndexOf('+');
            return corte > 0 ? bruto[..corte] : bruto;
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>
    /// <paramref name="candidata"/> e mais nova que o que esta rodando.
    ///
    /// Compara numero por numero, nunca texto: "0.10.0" é maior que "0.9.0" e
    /// a comparacao de texto diria o contrario. Sem conseguir interpretar os
    /// dois lados, devolve false — nao oferecer uma atualizacao real incomoda,
    /// oferecer uma inexistente destroi a confianca no aviso.
    /// </summary>
    public static bool EhMaisNova(string? candidata) =>
        Numero is { } atual
        && Version.TryParse(candidata, out var nova)
        && nova > atual;
}
