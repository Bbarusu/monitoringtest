using System.Windows;
using System.Windows.Controls;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// Tela da calibracao de ventoinhas. Mostra o que o app ja sabe de cada uma e
/// de onde sabe, e deixa medir a rotacao maxima de verdade.
/// </summary>
public partial class CalibracaoWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly CalibracaoService _calibracao;
    private readonly DispatcherTimerLeve _relogio;

    private CancellationTokenSource? _cancelamento;

    public CalibracaoWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);

        _cfg = cfg;
        _sensores = sensores;
        _calibracao = sensores.Calibracao;

        Montar();

        _relogio = new DispatcherTimerLeve(TimeSpan.FromSeconds(1), Montar);

        Closed += (_, _) =>
        {
            _fechada = true;
            _relogio.Parar();

            // Fechar no meio da calibracao nao pode deixar ventoinha presa em
            // 100%: cancela, e o finally do servico restaura.
            _cancelamento?.Cancel();
        };
    }

    // ------------------------------------------------------------------

    private bool _fechada;

    private void Montar()
    {
        // A calibracao continua rodando depois de a tela fechar, e o Montar do
        // bloco final cairia numa janela que ja saiu da tela.
        if (_fechada) return;

        var canais = _sensores.CanaisDeVentoinha();

        // A lista so e refeita quando a quantidade muda: remontar a cada segundo
        // enquanto o usuario le seria piscada a toa.
        if (Lista.Children.Count != canais.Count)
        {
            Lista.Children.Clear();
            foreach (var _ in canais) Lista.Children.Add(new LinhaDeVentoinha());
        }

        for (var i = 0; i < canais.Count; i++)
        {
            var c = canais[i];
            var teto = _sensores.Especificacoes.TetoDeVentoinha(c.Chave, TetoManual(c));

            ((LinhaDeVentoinha)Lista.Children[i]).Atualizar(c, teto);
        }

        if (canais.Count == 0)
        {
            CaixaEstado.Visibility = Visibility.Visible;
            Estado.Text = "Nenhuma ventoinha reporta rotação.";
        }

        BotaoCalibrar.IsEnabled = !_calibracao.Rodando && canais.Any(c => c.Comandavel);

        if (!_calibracao.Rodando && canais.Count > 0 && !canais.Any(c => c.Comandavel))
            Rodape.Text = "Nenhuma destas ventoinhas aceita comando por aqui.";

        Reposicionar();
    }

    /// <summary>
    /// O unico teto digitado a mao hoje e o da ventoinha da placa de video. As do
    /// gabinete nao tem campo: a calibracao resolve melhor que um numero chutado.
    /// </summary>
    private double? TetoManual(SensorService.CanalDeVentoinha c) =>
        c.Gpu && _cfg.RotacaoMaximaGpuRpm > 0 ? _cfg.RotacaoMaximaGpuRpm : null;

    // ------------------------------------------------------------------

    private async void Calibrar_Click(object sender, RoutedEventArgs e)
    {
        if (_calibracao.Rodando)
        {
            _cancelamento?.Cancel();
            return;
        }

        // A referencia local sobrevive ao campo, que o bloco final zera: e por
        // ela que se pergunta, depois do await, se houve cancelamento.
        var cancelamento = new CancellationTokenSource();
        _cancelamento = cancelamento;

        BotaoCalibrar.Content = "Cancelar";
        BotaoFechar.IsEnabled = false;
        CaixaEstado.Visibility = Visibility.Visible;

        var progresso = new Progress<string>(texto => Estado.Text = texto);

        try
        {
            var resultados = await _calibracao.CalibrarAsync(progresso, cancelamento.Token);

            // Cancelar no meio devolve a lista do jeito que estava, sem
            // excecao: sem esta conferencia, a tela anunciaria que nada foi
            // medido, como se a medicao tivesse falhado.
            if (cancelamento.IsCancellationRequested)
                Estado.Text = "Calibração cancelada. As ventoinhas voltaram ao normal.";
            else
                Relatar(resultados);
        }
        catch (OperationCanceledException)
        {
            Estado.Text = "Calibração cancelada. As ventoinhas voltaram ao normal.";
        }
        catch (Exception ex)
        {
            Log.Erro("Calibrar ventoinhas", ex);
            Estado.Text = "A calibração falhou. As ventoinhas voltaram ao normal.";
        }
        finally
        {
            cancelamento.Dispose();
            _cancelamento = null;

            BotaoCalibrar.Content = "Calibrar agora";
            BotaoFechar.IsEnabled = true;
            Montar();
        }
    }

    private void Relatar(List<ResultadoCalibracao> resultados)
    {
        var medidas = resultados.Where(r => r.Deu).ToList();
        var falhas = resultados.Where(r => !r.Deu).ToList();

        if (medidas.Count == 0)
        {
            Estado.Text = "Nenhuma rotação foi medida. " +
                          (falhas.FirstOrDefault()?.Problema ?? "");
            return;
        }

        var linhas = medidas
            .Select(r => $"{r.Nome}: {r.RpmMaximo:F0} rpm")
            .ToList();

        var texto = $"Pronto. {medidas.Count} ventoinha(s) medida(s):\n" +
                    string.Join("\n", linhas);

        if (falhas.Count > 0)
            texto += $"\n\n{falhas.Count} não respondeu(ram): " +
                     string.Join("; ", falhas.Select(f => f.Nome).Distinct());

        Estado.Text = texto;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Apaga tudo que foi medido e projetado. Util depois de trocar um
    /// ventilador: a curva antiga seguiria valendo para um conector que agora
    /// tem outra peca.
    /// </summary>
    private void Esquecer_Click(object sender, RoutedEventArgs e)
    {
        if (_calibracao.Rodando) return;

        _sensores.Especificacoes.Esquecer();

        CaixaEstado.Visibility = Visibility.Visible;
        Estado.Text = "Medições apagadas. Começaremos a aprender pelas leituras.";

        Montar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    private void Reposicionar() => Janelas.Ancorar(this, Owner);
}

/// <summary>Uma linha da lista: a ventoinha, a rotação de agora e o teto conhecido.</summary>
internal sealed class LinhaDeVentoinha : Border
{
    private readonly TextBlock _nome = new() { FontSize = 13, FontWeight = FontWeights.Medium };
    private readonly TextBlock _detalhe = new()
    {
        FontSize = 11,
        Margin = new Thickness(0, 2, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 250,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _agora = new()
    {
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right
    };

    public LinhaDeVentoinha()
    {
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 10, 12, 10);
        Margin = new Thickness(0, 0, 0, 8);
        SetResourceReference(BackgroundProperty, "Cartao");
        SetResourceReference(BorderBrushProperty, "Borda");

        _nome.SetResourceReference(TextBlock.ForegroundProperty, "Texto");
        _detalhe.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
        _agora.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        var grade = new Grid();
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grade.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(_nome);
        textos.Children.Add(_detalhe);
        Grid.SetColumn(textos, 0);
        grade.Children.Add(textos);

        Grid.SetColumn(_agora, 1);
        grade.Children.Add(_agora);

        Child = grade;
    }

    public void Atualizar(SensorService.CanalDeVentoinha canal, Teto teto)
    {
        _nome.Text = canal.Gpu ? $"{canal.Nome} · placa de vídeo" : canal.Nome;

        _agora.Text = !canal.TemLeitura
            ? "—"
            : canal.Rpm > 0
                ? teto.Existe ? $"{canal.Rpm:F0} / {teto.Valor:F0}" : $"{canal.Rpm:F0}"
                : "parada";

        _detalhe.Text = Procedencia(canal, teto);
        ToolTip = teto.Explicacao("rpm");
    }

    /// <summary>
    /// Por que esta ventoinha pode ou não ser calibrada.
    ///
    /// São três paradas diferentes no caminho, e juntá-las num recado só
    /// deixava o usuário sem saber o que fazer: sem leitura de rotação, sem
    /// canal de PWM, ou com PWM que a peça publica mas não aceita comandar —
    /// este último é o caso comum das placas de vídeo.
    /// </summary>
    private static string Procedencia(SensorService.CanalDeVentoinha canal, Teto teto)
    {
        if (!canal.TemLeitura)
            return "Sem leitura de rotação agora, então não há o que medir.";

        if (!canal.TemControle)
            return "A placa não expõe o PWM desta ventoinha: não há o que comandar.";

        if (!canal.Comandavel)
            return canal.Gpu
                ? "A placa de vídeo mostra o PWM mas não aceita comando do app. " +
                  "O teto é aprendido sozinho quando ela trabalha."
                : "A placa mostra o PWM mas não aceita comando do app.";

        return teto.Origem switch
        {
            Origem.Manual => "Teto informado por você.",
            Origem.Hardware => "Máximo medido na própria ventoinha.",
            Origem.Extrapolado => "Máximo projetado pela curva de PWM — calibrar confirma.",
            Origem.Medido => "Sem máximo conhecido: a barra usa o maior valor já visto.",
            _ => "Ainda sem leitura suficiente."
        };
    }
}
