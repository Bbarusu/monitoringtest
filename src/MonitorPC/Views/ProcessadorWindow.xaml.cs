using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// Quem esta usando a CPU, e como fechar.
///
/// Irma da tela de memoria, com uma diferenca que decide o desenho: memoria e
/// um valor que existe agora, e uso de CPU so existe entre duas leituras. Por
/// isso esta tela se atualiza sozinha e a de memoria nao precisa — e por isso
/// a primeira lista demora um instante para ter numero de verdade.
/// </summary>
public partial class ProcessadorWindow : Window
{
    /// <summary>
    /// Intervalo entre leituras. Curto demais mede ruido — qualquer processo
    /// parece disparar entre dois instantes muito proximos —, e longo demais
    /// atrasa demais o reflexo de fechar um programa.
    /// </summary>
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(2);

    private readonly SensorService _sensores;
    private readonly DispatcherTimerLeve _relogio;
    private bool _fechada;

    public ProcessadorWindow(SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);
        _sensores = sensores;

        // Primeira leitura sem numero: ela existe para a proxima ter de onde
        // tirar a diferenca. Dizer isso e melhor que mostrar uma lista inteira
        // zerada, que o usuario leria como "nada usando a CPU".
        PerformanceService.PorCpu();
        Subtitulo.Text = "Medindo o uso da CPU…";

        _relogio = new DispatcherTimerLeve(Intervalo, Montar);

        Closed += (_, _) =>
        {
            _fechada = true;
            _relogio.Parar();
        };
    }

    private void Montar()
    {
        if (_fechada) return;

        var uso = _sensores.Ultimo?.CpuUso;
        var temp = _sensores.Ultimo?.CpuTemp;

        Subtitulo.Text = (uso, temp) switch
        {
            ({ } u, { } t) when t > 0 => $"{u:F0}% de carga · {t:F0} °C",
            ({ } u, _) => $"{u:F0}% de carga",
            _ => "Processos por uso de CPU"
        };

        var processos = PerformanceService.PorCpu();

        // Remontar a lista inteira a cada dois segundos troca os controles
        // debaixo do cursor e faria o clique de fechar errar o alvo quando a
        // ordem mudasse. Só refaz quando a quantidade muda; o resto é
        // atualização no lugar.
        if (Lista.Children.Count != processos.Count)
        {
            Lista.Children.Clear();
            foreach (var _ in processos) Lista.Children.Add(new LinhaDeProcessoCpu(this));
        }

        for (var i = 0; i < processos.Count; i++)
            ((LinhaDeProcessoCpu)Lista.Children[i]).Atualizar(processos[i]);

        Reposicionar();
    }

    private void Reposicionar() => Janelas.Ancorar(this, Owner);

    // ------------------------------------------------------------------

    /// <summary>
    /// Qual programa está com a pergunta de forçar em aberto, e até quando.
    ///
    /// Mesmo desenho da tela de memória: sem janela, não existe pedido gentil,
    /// e forçar pede confirmação por um segundo clique. O prazo importa mais
    /// aqui, porque esta lista se reordena sozinha a cada leitura — a linha
    /// que estava sob o cursor pode não ser mais a mesma.
    /// </summary>
    private string? _forcarPendente;
    private DateTime _forcarAte;

    internal void Fechar(string nome)
    {
        if (_forcarPendente == nome && DateTime.UtcNow < _forcarAte)
        {
            _forcarPendente = null;

            var encerrados = PerformanceService.ForcarPorNome(nome);

            Rodape.Text = encerrados > 0
                ? $"{nome}: {encerrados} processo(s) encerrado(s) à força."
                : $"Não foi possível encerrar {nome}. O motivo ficou no erro.log.";

            return;
        }

        _forcarPendente = null;

        var r = PerformanceService.FecharPorNome(nome);

        if (r.Pedidos > 0)
        {
            Rodape.Text = $"{nome}: {r.Pedidos} pedido(s) de fechar enviados.";
            return;
        }

        if (r.SemJanela > 0 || r.Recusados > 0)
        {
            _forcarPendente = nome;
            _forcarAte = DateTime.UtcNow.AddSeconds(10);

            Rodape.Text = r.SemJanela > 0
                ? $"{nome} está em segundo plano e não tem janela para receber o " +
                  "pedido. Encerrar à força pode perder o que não foi salvo. " +
                  "Clique no X de novo para confirmar."
                : $"{nome} recebeu o pedido e não fechou — pode estar perguntando " +
                  "algo. Clique no X de novo para forçar.";

            return;
        }

        Rodape.Text = $"{nome} já não está em execução.";
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Uma linha da lista: o programa, quanto da CPU ele usa e o botão de fechar.
///
/// Reaproveitada entre leituras em vez de recriada, porque esta tela se
/// atualiza sozinha: recriar tudo a cada dois segundos trocaria os botões
/// debaixo do cursor no meio de um clique.
/// </summary>
internal sealed class LinhaDeProcessoCpu : Grid
{
    private readonly ProcessadorWindow _tela;

    private readonly TextBlock _nome = new()
    {
        FontSize = 13,
        FontWeight = FontWeights.Medium,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private readonly TextBlock _nota = new()
    {
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 0)
    };

    private readonly TextBlock _valor = new()
    {
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };

    private readonly Button _fechar;
    private string? _nomeDoProcesso;

    public LinhaDeProcessoCpu(ProcessadorWindow tela)
    {
        _tela = tela;

        Margin = new Thickness(0, 8, 0, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        _nota.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        _valor.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(_nome);
        textos.Children.Add(_nota);
        SetColumn(textos, 0);
        Children.Add(textos);

        SetColumn(_valor, 1);
        Children.Add(_valor);

        var x = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
            StrokeThickness = 1.8,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        };
        x.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextoFraco");

        _fechar = new Button
        {
            Style = (Style)tela.FindResource("BotaoIcone"),
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Content = x
        };

        _fechar.Click += (_, _) =>
        {
            if (_nomeDoProcesso is { } nome) _tela.Fechar(nome);
        };

        SetColumn(_fechar, 2);
        Children.Add(_fechar);
    }

    public void Atualizar(ProcessoCpu p)
    {
        _nomeDoProcesso = p.Nome;

        _nome.Text = p.Nome;
        _valor.Text = $"{p.CpuPct:F1}%";

        _nota.Text = p.Protegido
            ? "sistema ou segurança"
            : p.TemJanela ? "com janela aberta" : "em segundo plano";

        // Protegidos ficam na lista, para o quadro não parecer incompleto, mas
        // sem botão: fechar um deles deixaria o PC inutilizável ou desprotegido.
        _fechar.Visibility = p.Protegido ? Visibility.Collapsed : Visibility.Visible;

        _fechar.ToolTip = p.TemJanela
            ? $"Pedir para {p.Nome} fechar"
            : $"Encerrar {p.Nome} — está em segundo plano, então pede confirmação";

        ToolTip = p.Titulo;
    }
}
