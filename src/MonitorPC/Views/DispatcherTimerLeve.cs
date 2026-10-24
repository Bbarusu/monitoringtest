using System.Windows.Threading;

namespace MonitorPC.Views;

/// <summary>
/// Um temporizador de interface com parada simples. Existe so para as telas nao
/// repetirem a criacao e o descarte do DispatcherTimer.
/// </summary>
public sealed class DispatcherTimerLeve
{
    private readonly DispatcherTimer _timer;

    public DispatcherTimerLeve(TimeSpan intervalo, Action acao)
    {
        _timer = new DispatcherTimer { Interval = intervalo };
        _timer.Tick += (_, _) => acao();
        _timer.Start();
    }

    public void Parar() => _timer.Stop();
}
