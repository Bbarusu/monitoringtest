namespace MonitorPC.Models;

/// <summary>Uma leitura completa dos sensores, num instante.</summary>
public sealed class Snapshot
{
    public DateTime Instante { get; init; } = DateTime.Now;

    // CPU
    public float? CpuTemp { get; init; }
    public float? CpuUso { get; init; }
    public float? CpuWatts { get; init; }
    public float? CpuClockMhz { get; init; }
    /// <summary>Clock de cada nucleo fisico, em MHz.</summary>
    public IReadOnlyList<float> CpuClocksPorNucleo { get; init; } = Array.Empty<float>();

    /// <summary>Carga de cada thread logica, em porcentagem.</summary>
    public IReadOnlyList<float> CpuCargaPorThread { get; init; } = Array.Empty<float>();

    /// <summary>Carga da thread mais ocupada.</summary>
    public float? CpuPicoDeNucleo { get; init; }

    /// <summary>Tensao do nucleo, em volts.</summary>
    public float? CpuTensao { get; init; }

    // GPU
    public string? GpuNome { get; init; }
    public float? GpuTemp { get; init; }
    public float? GpuHotSpot { get; init; }
    public float? GpuUso { get; init; }
    public float? GpuWatts { get; init; }
    public float? VramUsadaMb { get; init; }
    public float? VramTotalMb { get; init; }
    public float? GpuFanRpm { get; init; }
    public float? GpuClockNucleo { get; init; }
    public float? GpuClockMemoria { get; init; }
    public float? GpuTempMemoria { get; init; }

    // RAM
    public float? RamUso { get; init; }
    public float? RamUsadaGb { get; init; }
    public float? RamTotalGb { get; init; }

    // Discos
    public float? DiscoTempMaior { get; init; }
    public float? DiscoAtividade { get; init; }
    public float? DiscoLeituraMbs { get; init; }
    public float? DiscoEscritaMbs { get; init; }

    /// <summary>Menor vida util entre as unidades: a que primeiro preocupa.</summary>
    public float? DiscoVidaMenor { get; init; }

    public IReadOnlyList<DiscoInfo> Discos { get; init; } = Array.Empty<DiscoInfo>();

    // Ventoinhas da placa-mae, pelo nome do sensor
    public IReadOnlyDictionary<string, float> Ventoinhas { get; init; }
        = new Dictionary<string, float>();

    // Energia
    public double WattsEstimados { get; init; }
    public double WattsTotais { get; init; }
    public double WhAcumulado { get; init; }
    public double WattsMedia { get; init; }
    public double WattsPico { get; init; }

    /// <summary>Quantidades em uso no calculo, e se vieram da deteccao.</summary>
    public int ModulosRam { get; init; }
    public int Discos2 { get; init; }
    public int VentoinhasAtivas { get; init; }

    /// <summary>Falso quando o driver de acesso em modo kernel nao carregou.</summary>
    public bool SensoresDeBaixoNivel { get; init; }
}
