namespace MonitorPC.Models;

/// <summary>Uma leitura crua, como o sensor a reporta.</summary>
public sealed record LeituraDeSensor(string Nome, string Tipo, float Valor, string Unidade);

/// <summary>Um componente e tudo que ele reporta.</summary>
public sealed record GrupoDeSensores(string Nome, string Tipo, IReadOnlyList<LeituraDeSensor> Leituras);
