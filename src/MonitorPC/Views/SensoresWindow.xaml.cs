using System.Windows;
using System.Windows.Controls;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class SensoresWindow : Window
{
    private readonly SensorService _sensores;
    private readonly DispatcherTimerLeve _relogio;

    public SensoresWindow(SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);

        _sensores = sensores;

        Montar();

        // A lista inteira e remontada a cada segundo: sao algumas dezenas de
        // linhas, e assim nenhum sensor novo fica de fora.
        _relogio = new DispatcherTimerLeve(TimeSpan.FromSeconds(1), Montar);
        Closed += (_, _) => _relogio.Parar();
    }

    private void Montar()
    {
        var filtro = Busca.Text.Trim();
        Dica.Visibility = string.IsNullOrEmpty(filtro) ? Visibility.Visible : Visibility.Collapsed;

        var grupos = _sensores.Tudo();
        var componentes = grupos.Count;
        var total = grupos.Sum(g => g.Leituras.Count);

        if (!string.IsNullOrEmpty(filtro))
            grupos = grupos
                .Select(g => g with
                {
                    Leituras = g.Leituras
                        .Where(l => Combina(l, g, filtro))
                        .ToList()
                })
                .Where(g => g.Leituras.Count > 0)
                .ToList();

        Subtitulo.Text = $"{total} leituras em {componentes} componentes";

        // Reconstroi so quando a estrutura muda; senao atualiza os valores no
        // lugar, para a rolagem nao saltar enquanto se le.
        if (!MesmaEstrutura(grupos))
        {
            Lista.Children.Clear();
            foreach (var grupo in grupos) Lista.Children.Add(Montar(grupo));
        }
        else
        {
            var i = 0;
            foreach (var grupo in grupos)
                if (Lista.Children[i++] is BlocoDeSensores bloco)
                    bloco.Atualizar(grupo);
        }

        Vazio.Visibility = grupos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Vazio.Text = "Nenhum sensor com esse filtro.";
    }

    private static bool Combina(LeituraDeSensor l, GrupoDeSensores g, string filtro) =>
        l.Nome.Contains(filtro, StringComparison.OrdinalIgnoreCase) ||
        l.Tipo.Contains(filtro, StringComparison.OrdinalIgnoreCase) ||
        l.Unidade.Contains(filtro, StringComparison.OrdinalIgnoreCase) ||
        g.Nome.Contains(filtro, StringComparison.OrdinalIgnoreCase);

    private bool MesmaEstrutura(List<GrupoDeSensores> grupos)
    {
        if (Lista.Children.Count != grupos.Count) return false;

        for (var i = 0; i < grupos.Count; i++)
            if (Lista.Children[i] is not BlocoDeSensores bloco ||
                !bloco.Corresponde(grupos[i]))
                return false;

        return true;
    }

    private static UIElement Montar(GrupoDeSensores grupo) => new BlocoDeSensores(grupo);

    private void Busca_Alterada(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        Montar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();
}
