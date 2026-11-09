namespace MonitorPC.Services;

public sealed class StressService : IDisposable
{
    private CancellationTokenSource? _cts;
    private readonly List<Thread> _threads = new();

    private double _resultado;

    public bool Rodando => _cts is { IsCancellationRequested: false };

    public DateTime? Inicio { get; private set; }

    public int ThreadsEmUso { get; private set; }

    public event Action<string>? Parou;

    public static int ThreadsDisponiveis => Environment.ProcessorCount;

    public void Iniciar(int threads)
    {
        if (Rodando) return;

        ThreadsEmUso = Math.Clamp(threads, 1, ThreadsDisponiveis);
        Inicio = DateTime.Now;
        _cts = new CancellationTokenSource();

        for (var i = 0; i < ThreadsEmUso; i++)
        {
            var thread = new Thread(Trabalhar)
            {
                IsBackground = true,
                Name = $"Estresse {i + 1}"
            };

            _threads.Add(thread);
            thread.Start(_cts.Token);
        }
    }

    public void Parar(string motivo)
    {
        if (_cts is null) return;

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _threads.Clear();
        Inicio = null;

        Parou?.Invoke(motivo);
    }

    private void Trabalhar(object? estado)
    {
        if (estado is not CancellationToken token) return;

        var x = 1.0000001;
        var soma = 0.0;

        while (!token.IsCancellationRequested)
        {
            for (var i = 0; i < 20_000; i++)
            {
                soma += Math.Sqrt(x) * Math.Sin(x);
                x += 0.0000001;
                if (x > 2) x = 1.0000001;
            }
        }

        lock (_threads) _resultado += soma;
    }

    public void Dispose() => Parar("encerrado");
}
