using System.Windows;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// O que esta usando a placa de video agora, programa por programa.
///
/// Serve para a pergunta que o uso total nao responde: a GPU esta em 40% com o
/// PC parado — quem esta puxando? Os contadores separam por motor, entao um
/// programa de fundo usando Compute aparece como o que e.
/// </summary>
public partial class GpuProcessosWindow : Window
{
    /// <summary>
    /// Uma linha a mais que o costume: aqui a lista e a tela inteira, e cortar
    /// em tres esconderia justamente o processo inesperado.
    /// </summary>
    private const int Linhas = 8;

    private readonly GpuProcessService _gpu;
    private readonly DispatcherTimerLeve _relogio;

    private bool _medindo;
    private bool _fechada;
    private bool _primeira = true;

    /// <summary>
    /// A consulta de contadores nasce e morre com a tela. Mante-la aberta o
    /// tempo todo nao custaria nada, mas tambem nao adiantaria: a ocupacao se
    /// calcula entre duas coletas, e so ha coleta com a tela aberta.
    /// </summary>
    public GpuProcessosWindow()
    {
        InitializeComponent();
        Janelas.Acompanhar(this);

        _gpu = new GpuProcessService();

        Sincronizar(Linhas);

        if (!_gpu.Disponivel)
        {
            Subtitulo.Text = "Não disponível neste Windows.";
            Aviso.Visibility = Visibility.Visible;
            Aviso.Text = _gpu.Motivo ?? "Os contadores de GPU não responderam.";

            ((LinhaDeDisco)Lista.Children[0]).Vazia("Sem leitura");
            for (var i = 1; i < Linhas; i++) ((LinhaDeDisco)Lista.Children[i]).Vazia("");

            _relogio = new DispatcherTimerLeve(TimeSpan.FromDays(1), () => { });
            Closed += (_, _) => { _fechada = true; _relogio.Parar(); _gpu.Dispose(); };
            return;
        }

        ((LinhaDeDisco)Lista.Children[0]).Vazia("Medindo…");
        for (var i = 1; i < Linhas; i++) ((LinhaDeDisco)Lista.Children[i]).Vazia("");

        Subtitulo.Text = "Quem está ocupando a placa agora, atualizado a cada segundo.";

        // Um segundo: a ocupação é medida por tempo, e um intervalo mais curto
        // daria um número que pula sem querer dizer nada.
        _relogio = new DispatcherTimerLeve(TimeSpan.FromSeconds(1), Atualizar);

        Closed += (_, _) =>
        {
            _fechada = true;
            _relogio.Parar();
            _gpu.Dispose();
        };
    }

    private async void Atualizar()
    {
        if (_medindo) return;
        _medindo = true;

        IReadOnlyList<ProcessoGpu> processos;

        // Fora da thread da interface: a leitura conversa com o subsistema de
        // contadores do Windows, que não é instantâneo.
        try
        {
            processos = await Task.Run(_gpu.Medir);
        }
        catch (Exception ex)
        {
            Log.Erro("Medir GPU por processo", ex);
            return;
        }
        finally
        {
            _medindo = false;
        }

        if (_fechada) return;

        // A primeira leitura sai logo depois da coleta de referência, com um
        // intervalo curto demais para valer: só a segunda diz alguma coisa.
        if (_primeira && processos.Count == 0)
        {
            _primeira = false;
            return;
        }

        _primeira = false;
        Mostrar(processos);
    }

    private void Mostrar(IReadOnlyList<ProcessoGpu> processos)
    {
        var maior = processos.Count > 0 ? processos.Max(p => p.UsoPct) : 0;

        for (var i = 0; i < Linhas; i++)
        {
            var linha = (LinhaDeDisco)Lista.Children[i];

            if (i >= processos.Count)
            {
                linha.Vazia(i == 0 ? "Nenhum programa usando a placa agora" : "");
                continue;
            }

            var p = processos[i];

            var detalhe = $"PID {p.Pid}";
            if (p.Motor is { } motor) detalhe += $" · {motor}";
            if (p.VramMb >= 1) detalhe += $" · {p.VramMb:F0} MB de VRAM";

            linha.Atualizar(
                p.Nome,
                detalhe,
                p.UsoPct >= 0.1 ? $"{p.UsoPct:0.0}%" : "—",

                // A barra compara com o maior da lista, e não com 100%: assim a
                // diferença entre o primeiro e o segundo fica visível mesmo
                // quando todos estão em poucos por cento.
                maior > 0 ? p.UsoPct / maior * 100 : 0);
        }
    }

    private void Sincronizar(int quantidade)
    {
        while (Lista.Children.Count > quantidade)
            Lista.Children.RemoveAt(Lista.Children.Count - 1);

        while (Lista.Children.Count < quantidade)
            Lista.Children.Add(new LinhaDeDisco());
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}
