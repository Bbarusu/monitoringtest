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

    public ConfiguracaoWindow(AppConfig cfg, SensorService sensores, bool primeiraVez)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);
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
            BotaoVoltar.Visibility = Visibility.Collapsed;
        }

        LimiteCpu.Value = Math.Clamp(_cfg.LimiteCpu, LimiteCpu.Minimum, LimiteCpu.Maximum);
        LimiteGpu.Value = Math.Clamp(_cfg.LimiteGpu, LimiteGpu.Minimum, LimiteGpu.Maximum);
        LimiteDisco.Value = Math.Clamp(_cfg.LimiteDisco, LimiteDisco.Minimum, LimiteDisco.Maximum);
        Notificar.IsChecked = _cfg.NotificarNoWindows;
        SugerirEconomico.IsChecked = _cfg.SugerirEconomico;
        MostrarVentoinhas.IsChecked = _cfg.MostrarVentoinhasNaCpu;
        VerificarAtualizacoes.IsChecked = _cfg.VerificarAtualizacoes;
        BlocoVentoinhas.Visibility = _cfg.MostrarVentoinhasNaCpu
            ? Visibility.Visible
            : Visibility.Collapsed;

        _carregando = false;
        AtualizarRotulos();

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

        MontarVentoinhas(s);

        foreach (var linha in _linhasDeVentoinha)
            linha.Atualizar(s.Ventoinhas.TryGetValue(linha.Nome, out var rpm) ? rpm : null);
    }

    private readonly List<LinhaDeSelecaoDeVentoinha> _linhasDeVentoinha = new();

    private void MontarVentoinhas(Snapshot s)
    {
        if (s.Ventoinhas.Count == 0) return;

        AjudaVentoinhas.Text =
            "Marque quais aparecem na aba CPU. A rotação ao lado é a de agora — " +
            "serve para descobrir qual conector é qual.";

        foreach (var nome in s.Ventoinhas.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (_linhasDeVentoinha.Any(l => l.Nome == nome)) continue;

            var chave = s.ChavesDeVentoinha.GetValueOrDefault(nome, nome);

            var linha = new LinhaDeSelecaoDeVentoinha(chave, nome, !_cfg.VentoinhasOcultas.Contains(chave));
            _linhasDeVentoinha.Add(linha);
            ListaVentoinhas.Children.Add(linha);
        }
    }

    private void MostrarVentoinhas_Alterado(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        BlocoVentoinhas.Visibility = MostrarVentoinhas.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

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
        _cfg.MostrarVentoinhasNaCpu = MostrarVentoinhas.IsChecked == true;
        _cfg.VerificarAtualizacoes = VerificarAtualizacoes.IsChecked == true;

        if (_linhasDeVentoinha.Count > 0)
        {
            var comLinha = _linhasDeVentoinha.Select(l => l.Chave).ToHashSet(StringComparer.Ordinal);

            var ocultas = _cfg.VentoinhasOcultas.Where(c => !comLinha.Contains(c)).ToList();
            ocultas.AddRange(_linhasDeVentoinha.Where(l => !l.Marcada).Select(l => l.Chave));

            _cfg.VentoinhasOcultas = ocultas;
        }

        _cfg.ConfiguracaoInicialFeita = true;
        _cfg.Salvar();

        DialogResult = true;
        Close();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}
