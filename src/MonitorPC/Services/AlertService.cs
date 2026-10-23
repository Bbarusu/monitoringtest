using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed record Alerta(string Componente, float Atual, double Limite)
{
    public string Titulo => $"Temperatura {Artigo} {Componente} acima do limite";
    public string Detalhe => $"{Atual:F0} °C agora · seu limite: {Limite:F0} °C";

    private string Artigo => Componente is "CPU" or "GPU" ? "da" : "do";
}

/// <summary>
/// Decide quando a temperatura passou do limite configurado.
///
/// O silencio e por componente: silenciar o disco nao deve esconder um aviso
/// da CPU, que e outro problema.
/// </summary>
public sealed class AlertService
{
    /// <summary>
    /// Uma margem para o aviso nao piscar: depois de disparar, so some quando a
    /// temperatura cai abaixo do limite menos isto.
    /// </summary>
    private const double Folga = 3;

    public static readonly TimeSpan DuracaoDoSilencio = TimeSpan.FromMinutes(10);

    private readonly AppConfig _cfg;
    private readonly Dictionary<string, DateTime> _silencio = new();
    private readonly HashSet<string> _emAlerta = new();

    /// <summary>Disparado uma vez por episodio de cada componente.</summary>
    public event Action<Alerta>? Comecou;

    public AlertService(AppConfig cfg) => _cfg = cfg;

    /// <summary>O aviso a mostrar agora, ja descontados os componentes silenciados.</summary>
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

        // Com mais de um componente acima do limite, mostra o que esta mais
        // longe dele: avisar de tudo ao mesmo tempo nao ajuda a decidir o que fazer.
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

            // Ja em alerta, segura ate cair da folga; senao o aviso piscaria a
            // cada decimo de grau em volta do limite.
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
