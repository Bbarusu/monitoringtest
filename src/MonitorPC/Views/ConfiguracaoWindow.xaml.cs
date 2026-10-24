using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class ConfiguracaoWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly bool _primeiraVez;
    private readonly Action<Snapshot> _aoLer;
    private bool _carregando = true;

    /// <param name="primeiraVez">
    /// Na primeira execucao a tela se apresenta como configuracao inicial e nao
    /// oferece voltar sem concluir. Depois, e so uma tela de ajustes.
    /// </param>
    public ConfiguracaoWindow(AppConfig cfg, SensorService sensores, bool primeiraVez)
    {
        InitializeComponent();
        _cfg = cfg;
        _sensores = sensores;
        _primeiraVez = primeiraVez;

        if (!primeiraVez)
        {
            Titulo.Text = "Ajustes";
            Subtitulo.Text = "Limites de temperatura e o que acontece ao ultrapassá-los.";
            TextoConcluir.Text = "Salvar";
            Rodape.Text = "As mudanças valem a partir da próxima leitura.";
        }
        else
        {
            // Sem configuracao salva, nao ha a que voltar.
            BotaoVoltar.Visibility = Visibility.Collapsed;
        }

        LimiteCpu.Value = Math.Clamp(_cfg.LimiteCpu, LimiteCpu.Minimum, LimiteCpu.Maximum);
        LimiteGpu.Value = Math.Clamp(_cfg.LimiteGpu, LimiteGpu.Minimum, LimiteGpu.Maximum);
        LimiteDisco.Value = Math.Clamp(_cfg.LimiteDisco, LimiteDisco.Minimum, LimiteDisco.Maximum);
        Notificar.IsChecked = _cfg.NotificarNoWindows;
        SugerirEconomico.IsChecked = _cfg.SugerirEconomico;

        _carregando = false;
        AtualizarRotulos();

        // As temperaturas de agora ajudam a escolher um limite que faca sentido
        // para este PC, em vez de um numero abstrato.
        _aoLer = snap => Dispatcher.BeginInvoke(() => MostrarAtuais(snap));
        _sensores.Atualizado += _aoLer;
        if (_sensores.Ultimo is { } ultimo) MostrarAtuais(ultimo);

        Closed += (_, _) => _sensores.Atualizado -= _aoLer;
    }

    private void MostrarAtuais(Snapshot s)
    {
        AgoraCpu.Text = s.CpuTemp is > 0 ? $"agora {s.CpuTemp:F0} °C" : "agora —";
        AgoraGpu.Text = s.GpuTemp is > 0 ? $"agora {s.GpuTemp:F0} °C" : "agora —";
        AgoraDisco.Text = s.DiscoTempMaior is > 0 ? $"agora {s.DiscoTempMaior:F0} °C" : "agora —";

        Alertar(AgoraCpu, s.CpuTemp, LimiteCpu.Value);
        Alertar(AgoraGpu, s.GpuTemp, LimiteGpu.Value);
        Alertar(AgoraDisco, s.DiscoTempMaior, LimiteDisco.Value);
    }

    /// <summary>Deixa visivel quando o limite escolhido ja esta sendo ultrapassado.</summary>
    private static void Alertar(TextBlock alvo, float? atual, double limite)
    {
        var passou = atual is { } v && v >= limite;
        alvo.Foreground = passou
            ? (Brush)Application.Current.Resources["AlertaTexto"]
            : (Brush)Application.Current.Resources["TextoFraco"];
    }

    private void Limite_Alterado(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        AtualizarRotulos();

        if (_carregando) return;
        if (_sensores.Ultimo is { } ultimo) MostrarAtuais(ultimo);
    }

    private void AtualizarRotulos()
    {
        ValorCpu.Text = $"{LimiteCpu.Value:F0} °C";
        ValorGpu.Text = $"{LimiteGpu.Value:F0} °C";
        ValorDisco.Text = $"{LimiteDisco.Value:F0} °C";
    }

    private void Concluir_Click(object sender, RoutedEventArgs e)
    {
        _cfg.LimiteCpu = LimiteCpu.Value;
        _cfg.LimiteGpu = LimiteGpu.Value;
        _cfg.LimiteDisco = LimiteDisco.Value;
        _cfg.NotificarNoWindows = Notificar.IsChecked == true;
        _cfg.SugerirEconomico = SugerirEconomico.IsChecked == true;
        _cfg.ConfiguracaoInicialFeita = true;
        _cfg.Salvar();

        DialogResult = true;
        Close();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Janelas.Ancorar(this, Owner);
    }
}
