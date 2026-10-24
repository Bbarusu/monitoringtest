namespace MonitorPC.Models;

/// <summary>
/// De onde veio o valor maximo que serve de escala para uma barra. A ordem e a
/// da confianca: o que a peca informa vale mais que o que o app deduz, que vale
/// mais que uma tabela, que vale mais que "o maior que ja vi".
/// </summary>
public enum Origem
{
    /// <summary>Nenhuma fonte disponivel. A barra nao e desenhada.</summary>
    Nenhuma,

    /// <summary>O maior valor que os sensores ja registraram.</summary>
    Medido,

    /// <summary>Tabela de especificacoes do fabricante, por modelo da peca.</summary>
    Catalogo,

    /// <summary>Calculado a partir de dois ou mais sensores da propria peca.</summary>
    Extrapolado,

    /// <summary>A peca informa o limite diretamente, por um sensor ou pelo SMART.</summary>
    Hardware,

    /// <summary>Informado a mao na tela de componentes. Sobrepoe tudo.</summary>
    Manual
}

/// <summary>Um valor maximo e a procedencia dele.</summary>
public sealed record Teto(double Valor, Origem Origem, string? Detalhe = null)
{
    public static readonly Teto Nenhum = new(0, Origem.Nenhuma);

    public bool Existe => Origem != Origem.Nenhuma && Valor > 0;

    /// <summary>Quanto de <paramref name="valor"/> cabe no teto, em porcentagem.</summary>
    public double Porcentagem(double valor) =>
        Existe ? Math.Clamp(valor / Valor * 100, 0, 100) : 0;

    /// <summary>Frase curta para o tooltip da barra, dizendo de onde saiu a escala.</summary>
    public string Explicacao(string unidade)
    {
        if (!Existe) return "Sem escala conhecida para esta medida.";

        var fonte = Origem switch
        {
            Origem.Manual => "valor que você informou",
            Origem.Hardware => "limite informado pela própria peça",
            Origem.Extrapolado => "calculado a partir dos sensores da peça",
            Origem.Catalogo => "ficha técnica do fabricante",
            _ => "maior valor já medido, que sobe conforme a peça é exigida"
        };

        var texto = $"Escala até {Valor:F0} {unidade} — {fonte}.";
        return Detalhe is null ? texto : $"{texto}\n{Detalhe}";
    }
}
