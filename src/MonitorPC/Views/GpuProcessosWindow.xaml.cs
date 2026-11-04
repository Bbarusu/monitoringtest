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
    /// Uma lista mais longa que o costume: aqui ela e a tela inteira, e cortar
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

        Sincronizar();

        if (!_gpu.Disponivel)
        {
            Subtitulo.Text = "Não disponível neste Windows.";
            CartaoAviso.Visibility = Visibility.Visible;
            Aviso.Text = _gpu.Motivo ?? "Os contadores de GPU não responderam.";

            Linha(0).Vazia("Sem leitura");

            _relogio = new DispatcherTimerLeve(TimeSpan.FromDays(1), () => { });
            Closed += (_, _) => { _fechada = true; _relogio.Parar(); _gpu.Dispose(); };
            return;
        }

        Subtitulo.Text = "Consumo de GPU";
        Linha(0).Vazia("Medindo…");

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

    private LinhaDeProcessoGpu Linha(int i) => (LinhaDeProcessoGpu)Lista.Children[i];

    private async void Atualizar()
    {
        if (_medindo) return;

        // Enquanto o cursor está sobre a lista, ela congela. A ordem muda a cada
        // leitura, e um botão de fechar que se move debaixo do cursor faria o
        // clique cair no programa errado.
        if (Lista.IsMouseOver) return;

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
        for (var i = 0; i < Linhas; i++)
        {
            var linha = Linha(i);

            if (i >= processos.Count)
            {
                linha.Vazia(i == 0 ? "Nenhum programa usando a placa agora" : "");
                continue;
            }

            var p = processos[i];
            linha.Atualizar(p, PerformanceService.EhProtegido(p.Nome, p.Pid));
        }
    }

    private void Sincronizar()
    {
        while (Lista.Children.Count > Linhas)
            Lista.Children.RemoveAt(Lista.Children.Count - 1);

        while (Lista.Children.Count < Linhas)
        {
            var linha = new LinhaDeProcessoGpu();
            linha.PediuParaFechar += Fechar;
            Lista.Children.Add(linha);
        }

        for (var i = 0; i < Linhas; i++) Linha(i).Vazia("");
    }

    /// <summary>
    /// Pede para o programa fechar, pelo PID e nao pelo nome: um navegador tem
    /// muitos processos, e so um deles costuma estar usando a placa. Fechar
    /// todos com o mesmo nome levaria o navegador inteiro junto.
    /// </summary>
    private void Fechar(ProcessoGpu p)
    {
        // Segundo clique no mesmo processo, logo depois do aviso: confirma
        // forçar. Um programa que usa a placa sem janela é justamente o caso
        // que esta tela existe para encontrar, e era o que o botão não fechava.
        if (_forcarPendente == p.Pid && DateTime.UtcNow < _forcarAte)
        {
            _forcarPendente = null;

            Rodape.Text = PerformanceService.Forcar(new[] { p.Pid }) > 0
                ? $"{p.Nome} foi encerrado à força."
                : $"Não foi possível encerrar {p.Nome}. O motivo ficou no erro.log.";

            return;
        }

        _forcarPendente = null;

        var (fechados, _, semJanela) = PerformanceService.Fechar(new[] { p.Pid });

        if (fechados > 0)
        {
            Rodape.Text = $"{p.Nome}: pedido de fechar enviado.";
            return;
        }

        _forcarPendente = p.Pid;
        _forcarAte = DateTime.UtcNow.AddSeconds(10);

        Rodape.Text = semJanela > 0
            ? $"{p.Nome} não tem janela para receber o pedido. Encerrar à força pode " +
              "perder o que não foi salvo. Clique de novo para confirmar."
            : $"{p.Nome} não aceitou fechar — pode estar em tela cheia ou perguntando " +
              "algo. Clique de novo para forçar.";
    }

    /// <summary>
    /// Qual processo está com a pergunta de forçar em aberto, e até quando. O
    /// prazo evita que a confirmação fique armada para um clique distraído
    /// minutos depois, numa lista que se reordena sozinha.
    /// </summary>
    private int? _forcarPendente;
    private DateTime _forcarAte;

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}
