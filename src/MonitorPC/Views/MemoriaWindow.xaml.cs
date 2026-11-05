using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class MemoriaWindow : Window
{
    private readonly SensorService _sensores;

    public MemoriaWindow(SensorService sensores)
    {
        InitializeComponent();
        _rodape = new RodapeComPrazo(Rodape);
        _rodape.Venceu += () => _forcarPendente = null;
        Janelas.Acompanhar(this);
        _sensores = sensores;

        Montar();
    }

    private void Montar()
    {
        var total = _sensores.Ultimo?.RamTotalGb;
        var usada = _sensores.Ultimo?.RamUsadaGb;

        Subtitulo.Text = usada is { } u && total is { } t
            ? $"{u:0.0} de {t:0} GB em uso"
            : "Processos por consumo de memória";

        Lista.Children.Clear();

        var processos = PerformanceService.PorMemoria();

        foreach (var p in processos)
            Lista.Children.Add(MontarLinha(p));

        // O Explorer costuma crescer com o tempo de uso; mostrar quanto ele ocupa
        // agora da a dimensao do que reinicia-lo devolve.
        var explorer = processos.FirstOrDefault(x =>
            x.Nome.Equals("explorer", StringComparison.OrdinalIgnoreCase));

        DetalheExplorer.Text = explorer is null
            ? ""
            : $"Ocupa {explorer.MemoriaMb:N0} MB · fecha as janelas de pastas abertas";
        DetalheExplorer.Visibility = explorer is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement MontarLinha(ProcessoMemoria p)
    {
        var linha = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(new TextBlock
        {
            Text = p.Nome,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Pincel("Texto")
        });

        var nota = p.Protegido
            ? "sistema ou segurança"
            : p.TemJanela ? "com janela aberta" : "em segundo plano";

        textos.Children.Add(new TextBlock
        {
            Text = nota,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = ThemeService.Pincel("TextoFraco")
        });
        Grid.SetColumn(textos, 0);
        linha.Children.Add(textos);

        var memoria = new TextBlock
        {
            Text = $"{p.MemoriaMb:N0} MB",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = ThemeService.Pincel("Texto")
        };
        Grid.SetColumn(memoria, 1);
        linha.Children.Add(memoria);

        // Protegidos aparecem na lista, para o total fazer sentido, mas sem
        // botao: fechar um deles deixaria o PC inutilizavel ou desprotegido.
        if (!p.Protegido)
        {
            var fechar = new Button
            {
                Style = (Style)FindResource("BotaoIcone"),
                Width = 32,
                Height = 32,
                // O que o botão faz muda conforme a linha, e a dica diz qual
                // dos dois: pedir para fechar não é a mesma coisa que encerrar.
                ToolTip = p.TemJanela
                    ? $"Pedir para {p.Nome} fechar"
                    : $"Encerrar {p.Nome} — está em segundo plano, então pede confirmação",
                Tag = p.Nome,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
                    Stroke = ThemeService.Pincel("TextoFraco"),
                    StrokeThickness = 1.8,
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform
                }
            };
            fechar.Click += Fechar_Click;
            Grid.SetColumn(fechar, 2);
            linha.Children.Add(fechar);
        }

        linha.ToolTip = $"{p.Titulo}\n\nClique para ver os arquivos abertos";
        linha.Cursor = System.Windows.Input.Cursors.Hand;

        var botao = new Button
        {
            Content = linha,
            Tag = p,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = (ControlTemplate)FindResource("LinhaClicavel")
        };
        botao.Click += Linha_Click;

        return botao;
    }

    private void Linha_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcessoMemoria p }) return;

        new ArquivosWindow(p.Pid, p.Nome) { Owner = this }.ShowDialog();
    }

    private void Fechar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string nome }) return;

        // O X fica dentro da linha clicavel: sem isto, fechar tambem abriria a
        // tela de arquivos.
        e.Handled = true;

        // Segundo clique no mesmo X, logo depois do aviso: é a confirmação de
        // forçar. Dois cliques no mesmo lugar em vez de uma caixa de diálogo
        // porque a pergunta nasce de um clique ali e a resposta volta para o
        // mesmo lugar — e porque uma caixa modal neste painel seria a única.
        if (_forcarPendente == nome && DateTime.UtcNow < _forcarAte)
        {
            _forcarPendente = null;

            var encerrados = PerformanceService.ForcarPorNome(nome);

            _rodape.Mostrar(encerrados > 0
                ? RodapeComPrazo.Plural(encerrados, $"{nome}: processo encerrado à força.",
                                         $"{nome}: {encerrados} processos encerrados à força.")
                : $"Não foi possível encerrar {nome}. O motivo ficou no erro.log.");

            Recarregar();
            return;
        }

        _forcarPendente = null;

        var r = PerformanceService.FecharPorNome(nome);

        if (r.Pedidos > 0)
        {
            _rodape.Mostrar(RodapeComPrazo.Plural(r.Pedidos, $"{nome}: pedido de fechar enviado.",
                                                $"{nome}: {r.Pedidos} pedidos de fechar enviados."));
        }
        else if (r.SemJanela > 0)
        {
            // Sem janela não existe pedido gentil: ou força, ou nada acontece.
            // Era este o caso que não fazia nada e não explicava nada.
            _forcarPendente = nome;
            _forcarAte = DateTime.UtcNow + RodapeComPrazo.Prazo;

            _rodape.Perguntar($"{nome} está em segundo plano e não tem janela para " +
                              "receber o pedido. Encerrar à força pode perder o que não " +
                              "foi salvo. Clique no X de novo para confirmar.");
        }
        else if (r.Recusados > 0)
        {
            _forcarPendente = nome;
            _forcarAte = DateTime.UtcNow + RodapeComPrazo.Prazo;

            _rodape.Perguntar($"{nome} recebeu o pedido e não fechou — pode estar " +
                              "perguntando algo. Clique no X de novo para forçar.");
        }
        else
        {
            _rodape.Mostrar($"{nome} já não está em execução.");
        }

        Recarregar();
    }

    /// <summary>
    /// Qual programa está com a pergunta de forçar em aberto, e até quando.
    ///
    /// O prazo existe para a confirmação não ficar armada: sem ele, um X
    /// clicado agora e outro daqui a dez minutos, na linha que mudou de lugar
    /// porque a lista se reordena por memória, encerrariam algo à força sem o
    /// usuário ter pedido.
    /// </summary>
    private string? _forcarPendente;
    private DateTime _forcarAte;
    private readonly RodapeComPrazo _rodape;

    /// <summary>
    /// Remonta a lista depois de um fechamento. Fechar leva tempo: a lista só
    /// reflete a mudança depois que o programa sai.
    /// </summary>
    private void Recarregar() => Dispatcher.InvokeAsync(async () =>
    {
        await Task.Delay(1200);
        Montar();
        Reposicionar();
    });

    /// <summary>
    /// O Explorer nao aparece na lista para ser fechado: encerra-lo sem subir de
    /// volta deixaria o PC sem barra de tarefas. Aqui e encerrar e subir junto.
    /// </summary>
    private async void Explorer_Click(object sender, RoutedEventArgs e)
    {
        BotaoExplorer.IsEnabled = false;
        _forcarPendente = null;
        _rodape.Mostrar("Reiniciando o Explorer…");

        var deuCerto = await Task.Run(PerformanceService.ReiniciarExplorer);

        // A area de notificacao e recriada pelo Explorer: o icone da bandeja
        // precisa ser reinstalado, senao o app fica sem porta de entrada.
        (Application.Current as App)?.RecriarIconeDaBandeja();

        _rodape.Mostrar(deuCerto
            ? "Explorer reiniciado. Janelas de pastas que estavam abertas foram fechadas."
            : "Não foi possível reiniciar o Explorer.");

        await Task.Delay(1500);
        Montar();
        Reposicionar();

        BotaoExplorer.IsEnabled = true;
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    private void Reposicionar()
    {
        Janelas.Ancorar(this, Owner);
    }

}
