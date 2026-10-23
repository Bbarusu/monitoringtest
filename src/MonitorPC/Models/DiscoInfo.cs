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
    float? TotalGb);
