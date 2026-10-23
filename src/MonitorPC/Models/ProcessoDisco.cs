namespace MonitorPC.Models;

/// <summary>Quanto um processo leu e escreveu em disco no ultimo intervalo.</summary>
public sealed record ProcessoDisco(int Pid, string Nome, double LeituraMbs, double EscritaMbs, string? Caminho = null)
{
    public double TotalMbs => LeituraMbs + EscritaMbs;
}
