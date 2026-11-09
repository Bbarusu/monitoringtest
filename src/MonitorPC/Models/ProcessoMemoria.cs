namespace MonitorPC.Models;

public sealed record ProcessoMemoria(
    int Pid,
    string Nome,
    string Titulo,
    double MemoriaMb,
    bool Protegido,
    bool TemJanela);
