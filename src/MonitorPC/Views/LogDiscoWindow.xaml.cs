using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class LogDiscoWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly DiskLogService _log;

    private readonly SensorService _sensores;
    private bool _montandoSeletor;

    public LogDiscoWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);
        _cfg = cfg;
        _sensores = sensores;
        _log = new DiskLogService(cfg);

        MontarSeletor();

        _sensores.Atualizado += AoLer;
        Closed += (_, _) => _sensores.Atualizado -= AoLer;

        Limite.Value = Math.Clamp(_cfg.LimiteLogMbs, Limite.Minimum, Limite.Maximum);
        AtualizarRotulos();

        _log.Mudou += () => Dispatcher.BeginInvoke(Montar);
        _log.Iniciar();

        Montar();

        Closed += (_, _) =>
        {
            _cfg.LimiteLogMbs = Limite.Value;
            _cfg.Salvar();
            _log.Dispose();
        };
    }

    private void AoLer(Snapshot s)
    {
        var ativo = s.Discos
            .Where(d => d.Atividade.HasValue)
            .OrderByDescending(d => d.Atividade)
            .FirstOrDefault();

        _log.DiscoMaisAtivo = ativo?.Atividade > 1 ? ativo.Nome : null;
    }

    private void MontarSeletor()
    {
        var discos = _sensores.Ultimo?.Discos ?? Array.Empty<DiscoInfo>();

        _montandoSeletor = true;
        SeletorDisco.Items.Clear();
        SeletorDisco.Items.Add("todas");
        foreach (var d in discos)
            SeletorDisco.Items.Add(d.Nome.Length <= 18 ? d.Nome : d.Nome[..17].TrimEnd() + "…");
        SeletorDisco.SelectedIndex = 0;
        SeletorDisco.Visibility = discos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _montandoSeletor = false;
    }

    private void SeletorDisco_Alterado(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _montandoSeletor) return;
        Montar();
    }

    private void AtualizarRotulos()
    {
        ValorLimite.Text = $"{Limite.Value:F0} MB/s";
        Subtitulo.Text = $"Últimos 15 min · acima de {Limite.Value:F0} MB/s";
    }

    private void Limite_Alterado(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;

        _cfg.LimiteLogMbs = Limite.Value;
        AtualizarRotulos();
        Montar();
    }

    private void Filtro_Alterado(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        Montar();
    }

    private void Montar()
    {
        var eventos = _log.Eventos.AsEnumerable();

        if (SeletorDisco.SelectedIndex > 0 &&
            _sensores.Ultimo?.Discos is { } discos &&
            SeletorDisco.SelectedIndex - 1 < discos.Count)
        {
            var nome = discos[SeletorDisco.SelectedIndex - 1].Nome;
            eventos = eventos.Where(x => x.DiscoAtivo == nome);
        }

        if (FiltroLeitura.IsChecked == true) eventos = eventos.Where(x => x.Tipo == "Leitura");
        else if (FiltroEscrita.IsChecked == true) eventos = eventos.Where(x => x.Tipo == "Escrita");
        else if (FiltroAltos.IsChecked == true) eventos = eventos.Where(x => x.Alto);

        var lista = eventos.Take(60).ToList();

        Lista.Children.Clear();

        foreach (var ev in lista)
            Lista.Children.Add(MontarLinha(ev));

        Vazio.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Vazio.Text = _log.Eventos.Count == 0
            ? "Sem registros por agora"
            : "Nenhum evento com este filtro.";
    }

    private UIElement MontarLinha(EventoDeDisco ev)
    {
        var linha = new Grid();
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hora = new TextBlock
        {
            Text = ev.Hora.ToString("HH:mm:ss"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.Pincel("TextoMuitoFraco")
        };
        Grid.SetColumn(hora, 0);
        linha.Children.Add(hora);

        var textos = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        textos.Children.Add(new TextBlock
        {
            Text = ev.Nome,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Pincel("Texto")
        });
        textos.Children.Add(new TextBlock
        {
            Text = ev.DiscoAtivo is null
                ? $"PID {ev.Pid} · leitura {ev.LeituraMbs:0.0} · escrita {ev.EscritaMbs:0.0} MB/s"
                : $"PID {ev.Pid} · {ev.DiscoAtivo} · {ev.LeituraMbs:0.0} / {ev.EscritaMbs:0.0} MB/s",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Pincel("TextoFraco")
        });
        Grid.SetColumn(textos, 1);
        linha.Children.Add(textos);

        var direita = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        direita.Children.Add(new TextBlock
        {
            Text = $"{ev.TotalMbs:0.0} MB/s",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            Foreground = ThemeService.Pincel("Texto")
        });
        direita.Children.Add(new TextBlock
        {
            Text = ev.Alto ? $"{ev.Tipo} · Alto" : ev.Tipo,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextAlignment = TextAlignment.Right,
            Foreground = (Brush)Application.Current.Resources[ev.Alto ? "AlertaTexto" : "TextoFraco"]
        });
        Grid.SetColumn(direita, 2);
        linha.Children.Add(direita);

        var dica = ev.Caminho ?? "Caminho indisponível (processo do sistema ou já encerrado)";
        if (ev.DiscoAtivo is not null)
            dica += $"\n\nUnidade mais ativa no momento: {ev.DiscoAtivo}\n" +
                    "Os contadores por processo não dizem em qual disco o acesso ocorreu.";
        linha.ToolTip = dica;

        var botao = new Button
        {
            Content = linha,
            Tag = ev,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = (ControlTemplate)FindResource("LinhaClicavel")
        };
        botao.Click += Evento_Click;

        return botao;
    }

    private void Evento_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EventoDeDisco ev }) return;

        new ArquivosWindow(ev.Pid, ev.Nome) { Owner = this }.ShowDialog();
    }

    private void Limpar_Click(object sender, RoutedEventArgs e)
    {
        _log.Limpar();
        Rodape.Text = "O registro corre enquanto esta tela estiver aberta.";
        Montar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}
