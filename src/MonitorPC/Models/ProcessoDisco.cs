namespace MonitorPC.Models;

public sealed record ProcessoDisco(int Pid, string Nome, double LeituraMbs, double EscritaMbs, string? Caminho = null)
{
    public double TotalMbs => LeituraMbs + EscritaMbs;
}
