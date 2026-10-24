namespace MonitorPC.Models;

/// <summary>Um programa e o quanto ele esta usando da placa de video.</summary>
public sealed record ProcessoGpu(
    int Pid,
    string Nome,
    double UsoPct,

    /// <summary>
    /// Que parte da placa ele ocupa: 3D, Compute, VideoDecode, VideoEncode,
    /// Copy. E o que separa um jogo de um minerador ou de uma chamada de video.
    /// </summary>
    string? Motor,

    /// <summary>Memoria dedicada da placa que o processo mantem reservada.</summary>
    double VramMb);
