using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed record Alerta(string Componente, float Atual, double Limite)
{
    public string Titulo => $"Temperatura {Artigo} {Componente} acima do limite";
    public string Detalhe => $"{Atual:F0} °C agora · seu limite: {Limite:F0} °C";

    private string Artigo => Componente == "CPU" || Componente == "GPU" ? "da" : "do";
}

/// <summary>
/// Decide quando a temperatura passou do limite configurado e por quanto tempo
/// o aviso fica silenciado.
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
    private DateTime _silencioAte = DateTime.MinValue;
    private string? _componenteEmAlerta;

    /// <summary>Disparado uma vez por episodio, para a notificacao do Windows.</summary>
    public event Action<Alerta>? Comecou;

    public AlertService(AppConfig cfg) => _cfg = cfg;

    public Alerta? Atual { get; private set; }

    public bool Silenciado => DateTime.UtcNow < _silencioAte;

    public void Silenciar() => _silencioAte = DateTime.UtcNow + DuracaoDoSilencio;

    public Alerta? Avaliar(Snapshot s)
    {
        var alerta = Pior(s);

        // Um episodio novo so e o mesmo enquanto for do mesmo componente.
        if (alerta is null)
        {
            _componenteEmAlerta = null;
            Atual = null;
            return null;
        }

        var novo = _componenteEmAlerta != alerta.Componente;
        _componenteEmAlerta = alerta.Componente;
        Atual = alerta;

        if (novo && !Silenciado) Comecou?.Invoke(alerta);

        return Silenciado ? null : alerta;
    }

    /// <summary>
    /// Com mais de um componente acima do limite, mostra o que esta mais longe
    /// dele: avisar de tudo ao mesmo tempo nao ajudaria a decidir o que fazer.
    /// </summary>
    private Alerta? Pior(Snapshot s)
    {
        var candidatos = new List<Alerta>();

        Considerar("CPU", s.CpuTemp, _cfg.LimiteCpu);
        Considerar("GPU", s.GpuTemp, _cfg.LimiteGpu);
        Considerar("disco", s.DiscoTempMaior, _cfg.LimiteDisco);

        return candidatos.Count == 0
            ? null
            : candidatos.OrderByDescending(a => a.Atual - a.Limite).First();

        void Considerar(string nome, float? atual, double limite)
        {
            if (atual is not { } v || v <= 0) return;

            // Ja em alerta, segura ate cair da folga; senao o aviso piscaria a
            // cada decimo de grau em volta do limite.
            var gatilho = _componenteEmAlerta == nome ? limite - Folga : limite;
            if (v >= gatilho) candidatos.Add(new Alerta(nome, v, limite));
        }
    }
}
