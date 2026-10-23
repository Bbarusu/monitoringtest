using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class LogDiscoWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly DiskLogService _log;

    public LogDiscoWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        _log = new DiskLogService(cfg);

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

    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------

    private void Montar()
    {
        var eventos = _log.Eventos.AsEnumerable();

        if (FiltroLeitura.IsChecked == true) eventos = eventos.Where(x => x.Tipo == "Leitura");
        else if (FiltroEscrita.IsChecked == true) eventos = eventos.Where(x => x.Tipo == "Escrita");
        else if (FiltroAltos.IsChecked == true) eventos = eventos.Where(x => x.Alto);

        // Mais que isto nao cabe na tela e so deixaria a rolagem pesada.
        var lista = eventos.Take(60).ToList();

        Lista.Children.Clear();

        foreach (var ev in lista)
            Lista.Children.Add(MontarLinha(ev));

        Vazio.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Vazio.Text = _log.Eventos.Count == 0
            ? "Nada registrado ainda. Use o PC normalmente e os picos de disco aparecem aqui."
            : "Nenhum evento com este filtro.";

        BotaoExportar.IsEnabled = _log.Eventos.Count > 0;
    }

    private UIElement MontarLinha(EventoDeDisco ev)
    {
        var linha = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hora = new TextBlock
        {
            Text = ev.Hora.ToString("HH:mm:ss"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextoMuitoFraco"]
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
            Foreground = (Brush)Application.Current.Resources["Texto"]
        });
        textos.Children.Add(new TextBlock
        {
            Text = $"PID {ev.Pid} · leitura {ev.LeituraMbs:0.0} · escrita {ev.EscritaMbs:0.0} MB/s",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextoFraco"]
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
            Foreground = (Brush)Application.Current.Resources["Texto"]
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

        linha.ToolTip = ev.Caminho ?? "Caminho indisponível (processo do sistema ou já encerrado)";
        return linha;
    }

    // ------------------------------------------------------------------

    private void Exportar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var arquivo = _log.Exportar();
            Rodape.Text = $"Salvo em {arquivo}";
            Rodape.Foreground = (Brush)Application.Current.Resources["TextoFraco"];

            // Abrir a pasta poupa procurar o arquivo.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{arquivo}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Erro("Exportar log", ex);
            Rodape.Text = "Não foi possível salvar o arquivo.";
        }

        Reposicionar();
    }

    private void Limpar_Click(object sender, RoutedEventArgs e)
    {
        _log.Limpar();
        Rodape.Text = "O registro corre enquanto esta tela estiver aberta.";
        Montar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    private void Reposicionar()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Reposicionar();
    }
}
