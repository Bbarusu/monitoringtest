namespace MonitorPC.Models;

public sealed record LeituraDeSensor(string Nome, string Tipo, float Valor, string Unidade);

public sealed record GrupoDeSensores(string Nome, string Tipo, IReadOnlyList<LeituraDeSensor> Leituras);
