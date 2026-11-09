using System.Windows.Controls;
using System.Windows.Threading;

namespace MonitorPC.Views;

internal sealed class RodapeComPrazo
{
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(10);

    private readonly TextBlock _alvo;
    private readonly string _padrao;
    private readonly DispatcherTimer _prazo;

    public event Action? Venceu;

    public RodapeComPrazo(TextBlock alvo)
    {
        _alvo = alvo;
        _padrao = alvo.Text;
        _prazo = new DispatcherTimer { Interval = Prazo };
        _prazo.Tick += (_, _) =>
        {
            _prazo.Stop();
            Venceu?.Invoke();
            Mostrar(_padrao);
        };
    }

    public void Mostrar(string texto)
    {
        _prazo.Stop();
        _alvo.Text = texto;
        _alvo.SetResourceReference(TextBlock.ForegroundProperty, "TextoMuitoFraco");
    }

    public void Perguntar(string texto)
    {
        _prazo.Stop();
        _alvo.Text = texto;
        _alvo.SetResourceReference(TextBlock.ForegroundProperty, "AlertaTexto");
        _prazo.Start();
    }

    public static string Plural(int n, string um, string varios) => n == 1 ? um : varios;
}
