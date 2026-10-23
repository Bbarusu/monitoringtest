namespace MonitorPC.Models;

/// <summary>Um processo com janela propria, candidato a ser fechado pelo usuario.</summary>
public sealed record ProcessoFechavel(int Pid, string Nome, string Titulo, double MemoriaMb);
