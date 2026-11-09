namespace MonitorPC.Models;

public sealed record ProcessoCpu(
    int Pid,
    string Nome,
    string Titulo,
    double CpuPct,
    bool Protegido,
    bool TemJanela);
