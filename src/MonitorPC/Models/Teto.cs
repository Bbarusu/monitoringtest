namespace MonitorPC.Models;

public enum Origem
{
    Nenhuma,

    Medido,

    Catalogo,

    Extrapolado,

    Hardware,

    Manual
}

public sealed record Teto(double Valor, Origem Origem, string? Detalhe = null)
{
    public static readonly Teto Nenhum = new(0, Origem.Nenhuma);

    public bool Existe => Origem != Origem.Nenhuma && Valor > 0;

    public double Porcentagem(double valor) =>
        Existe ? Math.Clamp(valor / Valor * 100, 0, 100) : 0;

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
