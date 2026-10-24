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
            ? "Fecha as janelas de pastas abertas e recria a barra de tarefas"
            : $"Ocupa {explorer.MemoriaMb:N0} MB · fecha as janelas de pastas abertas";
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
                ToolTip = $"Fechar {p.Nome}",
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

        var fechados = PerformanceService.FecharPorNome(nome);

        Rodape.Text = fechados == 0
            ? $"{nome} não aceitou fechar. Pode estar sem janela ou aguardando resposta."
            : $"{nome}: {fechados} pedido(s) de fechar enviados.";

        // Fechar leva tempo: a lista so reflete depois que o programa sai.
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1200);
            Montar();
            Reposicionar();
        });
    }

    /// <summary>
    /// O Explorer nao aparece na lista para ser fechado: encerra-lo sem subir de
    /// volta deixaria o PC sem barra de tarefas. Aqui e encerrar e subir junto.
    /// </summary>
    private async void Explorer_Click(object sender, RoutedEventArgs e)
    {
        BotaoExplorer.IsEnabled = false;
        Rodape.Text = "Reiniciando o Explorer…";

        var deuCerto = await Task.Run(PerformanceService.ReiniciarExplorer);

        // A area de notificacao e recriada pelo Explorer: o icone da bandeja
        // precisa ser reinstalado, senao o app fica sem porta de entrada.
        (Application.Current as App)?.RecriarIconeDaBandeja();

        Rodape.Text = deuCerto
            ? "Explorer reiniciado. Janelas de pastas que estavam abertas foram fechadas."
            : "Não foi possível reiniciar o Explorer.";

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
