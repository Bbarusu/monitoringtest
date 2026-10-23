namespace MonitorPC.Models;

/// <summary>Um processo e quanto de memoria ele ocupa agora.</summary>
public sealed record ProcessoMemoria(
    int Pid,
    string Nome,
    string Titulo,
    double MemoriaMb,
    bool Protegido,
    bool TemJanela);
