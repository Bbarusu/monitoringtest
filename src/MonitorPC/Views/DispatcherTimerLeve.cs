using System.Windows.Threading;

namespace MonitorPC.Views;

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
