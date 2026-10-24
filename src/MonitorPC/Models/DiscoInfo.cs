namespace MonitorPC.Models;

/// <summary>Uma unidade de armazenamento, como os sensores a reportam.</summary>
public sealed record DiscoInfo(
    string Nome,
    float? TemperaturaC,
    float? Atividade,
    float? LeituraMbs,
    float? EscritaMbs,
    float? VidaPct,
    float? HorasLigado,
    float? UsadoPct,
    float? LivreGb,
    float? TotalGb,

    /// <summary>
    /// Limiares termicos que a propria unidade publica pelo SMART. As abas nao
    /// os exibem como temperatura — sao valores fixos e altos, que ja foram
    /// confundidos com a leitura atual — mas como escala da barra sao o numero
    /// certo: vem do fabricante do disco.
    /// </summary>
    float? AvisoC = null,
    float? CriticoC = null);
