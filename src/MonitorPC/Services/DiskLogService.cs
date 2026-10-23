using System.IO;
using System.Text;
using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>Um momento em que um processo passou do limite de registro.</summary>
public sealed record EventoDeDisco(
    DateTime Hora,
    int Pid,
    string Nome,
    string? Caminho,
    double LeituraMbs,
    double EscritaMbs)
{
    public double TotalMbs => LeituraMbs + EscritaMbs;

    /// <summary>Leitura ou escrita, conforme o que dominou o momento.</summary>
    public string Tipo => LeituraMbs >= EscritaMbs ? "Leitura" : "Escrita";

    public bool Alto => TotalMbs >= DiskLogService.LimiteDeAlto;
}

/// <summary>
/// Registra quem passou do limite de uso de disco, enquanto a tela do log
/// estiver aberta. Guarda os ultimos 15 minutos e exporta em texto.
///
/// Isto ve processos, nao arquivos: saber qual arquivo cada um abriu exige
/// rastreamento de eventos do Windows, com custo bem maior.
/// </summary>
public sealed class DiskLogService : IDisposable
{
    public const double LimiteDeAlto = 50;

    private static readonly TimeSpan Janela = TimeSpan.FromMinutes(15);

    private readonly DiskProcessService _medidor = new();
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
            // A primeira amostra so serve de referencia.
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
                    agora, p.Pid, p.Nome, p.Caminho, p.LeituraMbs, p.EscritaMbs));
        }

        lock (_eventos)
            _eventos.RemoveAll(e => agora - e.Hora > Janela);
    }

    /// <summary>
    /// Grava o log em texto, com nome e caminho de cada processo, na pasta
    /// escolhida na configuracao.
    /// </summary>
    public string Exportar()
    {
        var pasta = string.IsNullOrWhiteSpace(_cfg.PastaDosLogs)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MonitorPC", "logs")
            : _cfg.PastaDosLogs;

        Directory.CreateDirectory(pasta);

        var arquivo = Path.Combine(pasta, $"disco-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
        var texto = new StringBuilder();

        texto.AppendLine("Monitor de PC — log de disco");
        texto.AppendLine($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        texto.AppendLine($"Registra processos acima de {_cfg.LimiteLogMbs:0.#} MB/s");
        texto.AppendLine();

        foreach (var e in Eventos)
        {
            texto.AppendLine($"{e.Hora:yyyy-MM-dd HH:mm:ss} | {e.Nome}");
            texto.AppendLine(e.Caminho ?? "caminho indisponível");
            texto.AppendLine(
                $"{e.Tipo} {e.TotalMbs:0.0} MB/s · leitura {e.LeituraMbs:0.0} · " +
                $"escrita {e.EscritaMbs:0.0} · PID {e.Pid}");
            texto.AppendLine();
        }

        if (Eventos.Count == 0) texto.AppendLine("Nenhum evento registrado.");

        File.WriteAllText(arquivo, texto.ToString(), Encoding.UTF8);
        return arquivo;
    }

    public void Dispose() => Parar();
}
