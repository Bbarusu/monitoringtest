namespace MonitorPC.Models;

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

    float? AvisoC = null,
    float? CriticoC = null);
