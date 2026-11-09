using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed record Alerta(string Componente, float Atual, double Limite)
{
    public string Titulo => $"Temperatura {Artigo} {Componente} acima do limite";
    public string Detalhe => $"{Atual:F0} °C agora · seu limite: {Limite:F0} °C";

    private string Artigo => Componente is "CPU" or "GPU" ? "da" : "do";
}

public sealed class AlertService
{
    private const double Folga = 3;

    public static readonly TimeSpan DuracaoDoSilencio = TimeSpan.FromMinutes(10);

    private readonly AppConfig _cfg;
    private readonly Dictionary<string, DateTime> _silencio = new();
    private readonly HashSet<string> _emAlerta = new();

    public event Action<Alerta>? Comecou;

    public AlertService(AppConfig cfg) => _cfg = cfg;

    public Alerta? Atual { get; private set; }

    public bool EstaSilenciado(string componente) =>
        _silencio.TryGetValue(componente, out var ate) && DateTime.UtcNow < ate;

    public void Silenciar(string componente) =>
        _silencio[componente] = DateTime.UtcNow + DuracaoDoSilencio;

    public Alerta? Avaliar(Snapshot s)
    {
        var candidatos = new List<Alerta>();

        Considerar("CPU", s.CpuTemp, _cfg.LimiteCpu);
        Considerar("GPU", s.GpuTemp, _cfg.LimiteGpu);
        Considerar("disco", s.DiscoTempMaior, _cfg.LimiteDisco);

        Atual = candidatos
            .Where(a => !EstaSilenciado(a.Componente))
            .OrderByDescending(a => a.Atual - a.Limite)
            .FirstOrDefault();

        return Atual;

        void Considerar(string nome, float? atual, double limite)
        {
            if (atual is not { } v || v <= 0)
            {
                _emAlerta.Remove(nome);
                return;
            }

            var jaEstava = _emAlerta.Contains(nome);
            var gatilho = jaEstava ? limite - Folga : limite;

            if (v < gatilho)
            {
                _emAlerta.Remove(nome);
                return;
            }

            var alerta = new Alerta(nome, v, limite);
            candidatos.Add(alerta);

            if (!jaEstava)
            {
                _emAlerta.Add(nome);
                if (!EstaSilenciado(nome)) Comecou?.Invoke(alerta);
            }
        }
    }
}
