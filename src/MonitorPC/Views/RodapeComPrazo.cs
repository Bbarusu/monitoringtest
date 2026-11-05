using System.Windows.Controls;
using System.Windows.Threading;

namespace MonitorPC.Views;

/// <summary>
/// O rodapé das telas de processos (Processamento, Memória, GPU por programa).
///
/// Duas vozes: a dica de sempre e os resultados ("pedido de fechar enviado")
/// no cinza apagado de sempre; a pergunta de forçar — a única mensagem que
/// pede um segundo clique, e só vale por 10 s — na mesma cor laranja do aviso
/// de risco da tela, para não passar por dica. Vencido o prazo, a pergunta
/// some sozinha e volta a dica: antes ela ficava escrita "clique de novo para
/// confirmar" depois de já não valer mais.
/// </summary>
internal sealed class RodapeComPrazo
{
    /// <summary>Quanto vale a pergunta de forçar.</summary>
    public static readonly TimeSpan Prazo = TimeSpan.FromSeconds(10);

    private readonly TextBlock _alvo;
    private readonly string _padrao;
    private readonly DispatcherTimer _prazo;

    /// <summary>A pergunta venceu sem resposta: quem perguntou desarma a confirmação.</summary>
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

    /// <summary>Resultado ou dica, no cinza de sempre.</summary>
    public void Mostrar(string texto)
    {
        _prazo.Stop();
        _alvo.Text = texto;
        _alvo.SetResourceReference(TextBlock.ForegroundProperty, "TextoMuitoFraco");
    }

    /// <summary>A pergunta de forçar: em laranja, e some depois do prazo.</summary>
    public void Perguntar(string texto)
    {
        _prazo.Stop();
        _alvo.Text = texto;
        _alvo.SetResourceReference(TextBlock.ForegroundProperty, "AlertaTexto");
        _prazo.Start();
    }

    /// <summary>"1 processo", "3 processos": sem o "(s)" de formulário.</summary>
    public static string Plural(int n, string um, string varios) => n == 1 ? um : varios;
}
