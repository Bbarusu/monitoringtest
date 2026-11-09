namespace MonitorPC.Models;

public sealed record ProcessoGpu(
    int Pid,
    string Nome,
    double UsoPct,

    string? Motor,

    double VramMb);
