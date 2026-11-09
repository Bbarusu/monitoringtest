using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class EstresseWindow : Window
{
    private const double Folga = 2;

    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly StressService _estresse = new();
    private readonly Action<Snapshot> _aoLer;

    private float? _tempAntes;
    private float? _clockAntes;
    private float _tempPico;
    private float _wattsPico;
    private float _clockPico;
    private float _clockMinimo = float.MaxValue;
    private DateTime _fim;

    public EstresseWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);

        _cfg = cfg;
        _sensores = sensores;

        Threads.Maximum = StressService.ThreadsDisponiveis;
        Threads.Value = StressService.ThreadsDisponiveis;
        AtualizarRotulos();

        _aoLer = snap => Dispatcher.BeginInvoke(() => Aplicar(snap));
        _sensores.Atualizado += _aoLer;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);

        _estresse.Parou += motivo => Dispatcher.BeginInvoke(() => AoParar(motivo));

        Closed += (_, _) =>
        {
            _sensores.Atualizado -= _aoLer;
            _estresse.Dispose();
        };
    }

    private void AtualizarRotulos()
    {
        ValorThreads.Text = $"{Threads.Value:F0}";
        ValorDuracao.Text = $"{Duracao.Value:F0} min";

        var total = StressService.ThreadsDisponiveis;
        DetalheThreads.Text = Threads.Value >= total
            ? $"Todas as {total} threads do processador"
            : $"{Threads.Value:F0} de {total} · o resto fica livre";

        TextoAviso.Text =
            $"A CPU vai ao seu limite, e o PC pode ficar lento durante o teste. Ele para sozinho ao chegar perto de {_cfg.LimiteCpu:F0} °C, o limite que você configurou, ou ao fim do tempo. Recomendamos salvar e fechar o que estiver fazendo.";
    }

    private void Ajuste_Alterado(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        AtualizarRotulos();
    }

    private void Aplicar(Snapshot s)
    {
        TempAgora.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0}" : "--";
        UsoAgora.Text = s.CpuUso is { } u ? $"{u:F0}%" : "--";
        ClockAgora.Text = s.CpuClockMhz is > 0 ? $"{s.CpuClockMhz / 1000:0.00} GHz" : "n/d";
        WattsAgora.Text = s.CpuWatts is > 0 ? $"{s.CpuWatts:F0} W" : "n/d";

        if (!_estresse.Rodando)
        {
            _tempAntes = s.CpuTemp;
            _clockAntes = s.CpuClockMhz;
            return;
        }

        Medir(s);

        var restante = _fim - DateTime.Now;
        Tempo.Text = restante > TimeSpan.Zero
            ? $"{_estresse.ThreadsEmUso} threads · faltam {restante:mm\\:ss}"
            : $"{_estresse.ThreadsEmUso} threads";

        if (s.CpuTemp is { } t && t >= _cfg.LimiteCpu - Folga)
        {
            _estresse.Parar($"a CPU chegou a {t:F0} °C");
            return;
        }

        if (restante <= TimeSpan.Zero) _estresse.Parar("o tempo acabou");
    }

    private void Medir(Snapshot s)
    {
        if (s.CpuTemp is { } t) _tempPico = Math.Max(_tempPico, t);
        if (s.CpuWatts is { } w) _wattsPico = Math.Max(_wattsPico, w);

        if (s.CpuClockMhz is { } c && c > 0)
        {
            _clockPico = Math.Max(_clockPico, c);

            if ((DateTime.Now - (_estresse.Inicio ?? DateTime.Now)).TotalSeconds > 10)
                _clockMinimo = Math.Min(_clockMinimo, c);
        }
    }

    private void Acao_Click(object sender, RoutedEventArgs e)
    {
        if (_estresse.Rodando)
        {
            _estresse.Parar("você parou o teste");
            return;
        }

        _tempPico = 0;
        _wattsPico = 0;
        _clockPico = 0;
        _clockMinimo = float.MaxValue;
        _fim = DateTime.Now.AddMinutes(Duracao.Value);

        AreaResultado.Visibility = Visibility.Collapsed;
        AreaAjustes.IsEnabled = false;
        Estado.Text = "Sob carga";
        TextoAcao.Text = "Parar agora";

        _estresse.Iniciar((int)Threads.Value);
    }

    private void AoParar(string motivo)
    {
        AreaAjustes.IsEnabled = true;
        Estado.Text = "Pronto";
        Tempo.Text = "";
        TextoAcao.Text = "Iniciar teste";

        MostrarResultado(motivo);
        Janelas.Ancorar(this, Owner);
    }

    private void MostrarResultado(string motivo)
    {
        Resultado.Children.Clear();

        Resultado.Children.Add(Titulo($"Teste encerrado: {motivo}"));

        if (_tempPico > 0)
            Resultado.Children.Add(Linha("Temperatura máxima",
                _tempAntes is { } antes && antes > 0
                    ? $"{_tempPico:F0} °C  (de {antes:F0} °C em repouso)"
                    : $"{_tempPico:F0} °C"));

        if (_wattsPico > 0)
            Resultado.Children.Add(Linha("Potência máxima", $"{_wattsPico:F0} W"));

        if (_clockPico > 0)
            Resultado.Children.Add(Linha("Clock sob carga",
                _clockMinimo < float.MaxValue
                    ? $"{_clockMinimo / 1000:0.00} a {_clockPico / 1000:0.00} GHz"
                    : $"{_clockPico / 1000:0.00} GHz"));

        if (_clockPico > 0 && _clockMinimo < float.MaxValue)
        {
            var queda = (_clockPico - _clockMinimo) / _clockPico * 100;

            Resultado.Children.Add(queda >= 10
                ? Observacao($"O clock caiu {queda:F0}% durante o teste, o que indica redução " +
                             "térmica: a CPU baixou a frequência para não esquentar mais. " +
                             "Vale olhar o cooler e o fluxo de ar do gabinete.", alerta: true)
                : Observacao($"O clock se manteve durante o teste, com variação de {queda:F0}%. " +
                             "A refrigeração deu conta da carga.", alerta: false));
        }

        AreaResultado.Visibility = Visibility.Visible;
    }

    private static TextBlock Titulo(string texto)
    {
        var bloco = new TextBlock
        {
            Text = texto,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        bloco.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        return bloco;
    }

    private static UIElement Linha(string rotulo, string valor)
    {
        var grade = new Grid { Margin = new Thickness(0, 0, 0, 6) };

        var esquerda = new TextBlock { Text = rotulo, FontSize = 12 };
        esquerda.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        var direita = new TextBlock
        {
            Text = valor,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        direita.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        grade.Children.Add(esquerda);
        grade.Children.Add(direita);
        return grade;
    }

    private static TextBlock Observacao(string texto, bool alerta)
    {
        var bloco = new TextBlock
        {
            Text = texto,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        bloco.SetResourceReference(TextBlock.ForegroundProperty,
            alerta ? "AlertaTexto" : "TextoMuitoFraco");
        return bloco;
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}
