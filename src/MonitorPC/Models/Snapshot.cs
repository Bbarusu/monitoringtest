namespace MonitorPC.Models;

public sealed class Snapshot
{
    public float? CpuTemp { get; init; }
    public float? CpuUso { get; init; }
    public float? CpuWatts { get; init; }
    public float? CpuClockMhz { get; init; }
    public IReadOnlyList<float> CpuClocksPorNucleo { get; init; } = Array.Empty<float>();

    public IReadOnlyList<float> CpuCargaPorThread { get; init; } = Array.Empty<float>();

    public float? CpuPicoDeNucleo { get; init; }

    public float? CpuTensao { get; init; }

    public string? GpuNome { get; init; }
    public float? GpuTemp { get; init; }
    public float? GpuHotSpot { get; init; }
    public float? GpuUso { get; init; }
    public float? GpuWatts { get; init; }
    public float? VramUsadaMb { get; init; }
    public float? VramTotalMb { get; init; }

    public bool VramCompartilhada { get; init; }
    public float? GpuFanRpm { get; init; }

    public float? GpuFanPwm { get; init; }

    public string? GpuFanChave { get; init; }

    public float? GpuClockNucleo { get; init; }
    public float? GpuClockMemoria { get; init; }
    public float? GpuTempMemoria { get; init; }

    public float? RamUso { get; init; }
    public float? RamUsadaGb { get; init; }
    public float? RamTotalGb { get; init; }

    public float? DiscoTempMaior { get; init; }
    public float? DiscoAtividade { get; init; }
    public float? DiscoLeituraMbs { get; init; }
    public float? DiscoEscritaMbs { get; init; }

    public float? DiscoVidaMenor { get; init; }

    public IReadOnlyList<DiscoInfo> Discos { get; init; } = Array.Empty<DiscoInfo>();

    public IReadOnlyDictionary<string, float> Ventoinhas { get; init; }
        = new Dictionary<string, float>();

    public IReadOnlyDictionary<string, float> ControlesDeVentoinha { get; init; }
        = new Dictionary<string, float>();

    public IReadOnlyDictionary<string, string> ChavesDeVentoinha { get; init; }
        = new Dictionary<string, string>();

    public double WhDeHoje { get; init; }

    public double WhRegistrado { get; init; }

    public double WattsEstimados { get; init; }
    public double WattsTotais { get; init; }
    public double WhAcumulado { get; init; }
    public double WattsMedia { get; init; }
    public double WattsPico { get; init; }

    public int ModulosRam { get; init; }
    public int Discos2 { get; init; }
    public int VentoinhasAtivas { get; init; }

    public bool SensoresDeBaixoNivel { get; init; }
}
