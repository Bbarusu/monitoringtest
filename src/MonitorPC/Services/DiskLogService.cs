using MonitorPC.Models;

namespace MonitorPC.Services;

public sealed record EventoDeDisco(
    DateTime Hora,
    int Pid,
    string Nome,
    string? Caminho,
    double LeituraMbs,
    double EscritaMbs,
    string? DiscoAtivo)
{
    public double TotalMbs => LeituraMbs + EscritaMbs;

    public string Tipo => LeituraMbs >= EscritaMbs ? "Leitura" : "Escrita";

    public bool Alto => TotalMbs >= DiskLogService.LimiteDeAlto;
}

public sealed class DiskLogService : IDisposable
{
    public const double LimiteDeAlto = 50;

    private static readonly TimeSpan Janela = TimeSpan.FromMinutes(15);

    private readonly DiskProcessService _medidor = new();

    public string? DiscoMaisAtivo { get; set; }
    private readonly List<EventoDeDisco> _eventos = new();
    private readonly AppConfig _cfg;
    private CancellationTokenSource? _cts;

    public DiskLogService(AppConfig cfg) => _cfg = cfg;

    public event Action? Mudou;

    public bool Ativo => _cts is not null;

    public IReadOnlyList<EventoDeDisco> Eventos
    {
        get
        {
            lock (_eventos) return _eventos.OrderByDescending(e => e.Hora).ToList();
        }
    }

    public void Iniciar()
    {
        if (_cts is not null) return;

        _medidor.Reiniciar();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            try { await Task.Delay(1000, token); } catch { return; }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    Amostrar();
                    Mudou?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Erro("Log de disco", ex);
                }

                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    public void Parar()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Limpar()
    {
        lock (_eventos) _eventos.Clear();
        Mudou?.Invoke();
    }

    private void Amostrar()
    {
        var agora = DateTime.Now;
        var limite = Math.Max(_cfg.LimiteLogMbs, 0.1);

        foreach (var p in _medidor.MedirTudo())
        {
            if (p.TotalMbs < limite) continue;

            lock (_eventos)
                _eventos.Add(new EventoDeDisco(
                    agora, p.Pid, p.Nome, p.Caminho, p.LeituraMbs, p.EscritaMbs, DiscoMaisAtivo));
        }

        lock (_eventos)
            _eventos.RemoveAll(e => agora - e.Hora > Janela);
    }

    public void Dispose() => Parar();
}
