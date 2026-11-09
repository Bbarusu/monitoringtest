using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class ProcessadorWindow : Window
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(2);

    private readonly SensorService _sensores;
    private readonly DispatcherTimerLeve _relogio;
    private bool _fechada;

    public ProcessadorWindow(SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);
        _sensores = sensores;
        _rodape = new RodapeComPrazo(Rodape);
        _rodape.Venceu += () => _forcarPendente = null;

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

    private string? _forcarPendente;
    private DateTime _forcarAte;
    private readonly RodapeComPrazo _rodape;

    internal void Fechar(string nome)
    {
        if (_forcarPendente == nome && DateTime.UtcNow < _forcarAte)
        {
            _forcarPendente = null;

            var encerrados = PerformanceService.ForcarPorNome(nome);

            _rodape.Mostrar(encerrados > 0
                ? RodapeComPrazo.Plural(encerrados, $"{nome}: processo encerrado à força.",
                                         $"{nome}: {encerrados} processos encerrados à força.")
                : $"Não foi possível encerrar {nome}. O motivo ficou no erro.log.");

            return;
        }

        _forcarPendente = null;

        var r = PerformanceService.FecharPorNome(nome);

        if (r.Pedidos > 0)
        {
            _rodape.Mostrar(RodapeComPrazo.Plural(r.Pedidos, $"{nome}: pedido de fechar enviado.",
                                                $"{nome}: {r.Pedidos} pedidos de fechar enviados."));
            return;
        }

        if (r.SemJanela > 0 || r.Recusados > 0)
        {
            _forcarPendente = nome;
            _forcarAte = DateTime.UtcNow + RodapeComPrazo.Prazo;

            _rodape.Perguntar(r.SemJanela > 0
                ? $"{nome} está em segundo plano e não tem janela para receber o " +
                  "pedido. Encerrar à força pode perder o que não foi salvo. " +
                  "Clique no X de novo para confirmar."
                : $"{nome} recebeu o pedido e não fechou — pode estar perguntando " +
                  "algo. Clique no X de novo para forçar.");

            return;
        }

        _rodape.Mostrar($"{nome} já não está em execução.");
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}

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

        _fechar.Visibility = p.Protegido ? Visibility.Collapsed : Visibility.Visible;

        _fechar.ToolTip = p.TemJanela
            ? $"Pedir para {p.Nome} fechar"
            : $"Encerrar {p.Nome} — está em segundo plano, então pede confirmação";

        ToolTip = p.Titulo;
    }
}
