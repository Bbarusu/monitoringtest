using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class ComponentesWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;

    private LinhaDeAjuste _ram = null!;
    private LinhaDeAjuste _discos = null!;
    private LinhaDeAjuste _ventoinhas = null!;

    public ComponentesWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        _cfg = cfg;
        _sensores = sensores;

        MontarMedidos();
        MontarQuantidades();
        MontarWatts();
        MontarFonte();

        Atualizar();
    }

    // ------------------------------------------------------------------

    /// <summary>CPU e GPU tem sensor de potencia: nada a configurar aqui.</summary>
    private void MontarMedidos()
    {
        var s = _sensores.Ultimo;

        Medidos.Children.Add(Medido("CPU", s?.CpuWatts is { } w ? $"{w:F0} W agora" : "sem leitura"));
        Medidos.Children.Add(Medido(
            s?.GpuNome ?? "GPU",
            s?.GpuWatts is { } g ? $"{g:F0} W agora" : "sem leitura"));
    }

    private static UIElement Medido(string nome, string detalhe)
    {
        var caixa = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        caixa.SetResourceReference(BackgroundProperty, "Cartao");
        caixa.SetResourceReference(BorderBrushProperty, "Borda");

        var grade = new Grid();
        var textos = new StackPanel();

        var titulo = new TextBlock { Text = nome, FontSize = 13, FontWeight = FontWeights.Medium };
        titulo.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

        var sub = new TextBlock { Text = detalhe, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");

        textos.Children.Add(titulo);
        textos.Children.Add(sub);
        grade.Children.Add(textos);

        var marca = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3, 8, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x23, 0x3D)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "medido",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xBA, 0xFF))
            }
        };
        grade.Children.Add(marca);

        caixa.Child = grade;
        return caixa;
    }

    // ------------------------------------------------------------------

    private void MontarQuantidades()
    {
        _ram = new LinhaDeAjuste(LinhaDeAjuste.Modo.Contador, "Módulos de RAM", "",
            _sensores.ModulosRam.Valor, 0, 16,
            v => { _cfg.ModulosRamManual = (int)v; Salvar(); });

        _discos = new LinhaDeAjuste(LinhaDeAjuste.Modo.Contador, "SSDs e HDs", "",
            _sensores.Discos.Valor, 0, 16,
            v => { _cfg.DiscosManual = (int)v; Salvar(); });

        _ventoinhas = new LinhaDeAjuste(LinhaDeAjuste.Modo.Contador, "Ventoinhas", "",
            _sensores.Ventoinhas.Valor, 0, 20,
            v => { _cfg.VentoinhasManual = (int)v; Salvar(); });

        Quantidades.Children.Add(_ram);
        Quantidades.Children.Add(_discos);
        Quantidades.Children.Add(_ventoinhas);

        // Nenhum sensor informa estes: so o usuario sabe.
        Quantidades.Children.Add(new LinhaDeAjuste(LinhaDeAjuste.Modo.Contador,
            "Peças com LED ou RGB", "Nenhum sensor detecta: informe quantas são",
            _cfg.PecasComLed, 0, 30,
            v => { _cfg.PecasComLed = (int)v; Salvar(); }));

        Quantidades.Children.Add(new LinhaDeAjuste(LinhaDeAjuste.Modo.Contador,
            "Bombas de water cooler", "Nenhum sensor detecta: 0 se o cooler for a ar",
            _cfg.TemWaterCooler ? 1 : 0, 0, 2,
            v => { _cfg.TemWaterCooler = v >= 1; Salvar(); }));
    }

    private void MontarWatts()
    {
        Watts.Children.Add(Linha("Por módulo de RAM", "Valor típico: 3 W",
            _cfg.WattsPorModuloRam, v => _cfg.WattsPorModuloRam = v));

        Watts.Children.Add(Linha("Por SSD ou HD", "SSD fica perto de 2 W; HD mecânico passa de 6",
            _cfg.WattsPorDisco, v => _cfg.WattsPorDisco = v));

        Watts.Children.Add(Linha("Por ventoinha", "Valor típico: 1,5 W",
            _cfg.WattsPorVentoinha, v => _cfg.WattsPorVentoinha = v));

        Watts.Children.Add(Linha("Por peça com LED", "Valor típico: 1,5 W",
            _cfg.WattsPorLed, v => _cfg.WattsPorLed = v));

        Watts.Children.Add(Linha("Bomba do water cooler", "Valor típico: 6 W",
            _cfg.WattsBombaWaterCooler, v => _cfg.WattsBombaWaterCooler = v));

        Watts.Children.Add(Linha("Placa-mãe e chipset", "Valor típico: 12 W",
            _cfg.WattsPlacaMae, v => _cfg.WattsPlacaMae = v));

        Watts.Children.Add(Linha("Outros dispositivos", "USB, placas extras, periféricos",
            _cfg.WattsOutros, v => _cfg.WattsOutros = v));
    }

    private void MontarFonte()
    {
        Fonte.Children.Add(new LinhaDeAjuste(LinhaDeAjuste.Modo.Numero,
            "Capacidade da fonte", "Usada como escala do anel de potência",
            _cfg.CapacidadeFonteW, 100, 2000,
            v => { _cfg.CapacidadeFonteW = (int)v; Salvar(); }, "W"));

        Fonte.Children.Add(new LinhaDeAjuste(LinhaDeAjuste.Modo.Numero,
            "Eficiência da fonte", "80 Plus Bronze fica em 85%; Gold, em 90%",
            Math.Round(_cfg.EficienciaFonte * 100), 70, 96,
            v => { _cfg.EficienciaFonte = v / 100.0; Salvar(); }, "%"));
    }

    private LinhaDeAjuste Linha(string titulo, string explicacao, double valor, Action<double> definir) =>
        new(LinhaDeAjuste.Modo.Numero, titulo, explicacao, valor, 0, 200,
            v => { definir(v); Salvar(); }, "W");

    // ------------------------------------------------------------------

    /// <summary>Salva e manda o serviço recalcular, para o painel refletir na hora.</summary>
    private void Salvar()
    {
        _cfg.Salvar();
        _sensores.AplicarConfiguracao();
        Atualizar();
    }

    private void Atualizar()
    {
        _ram.Explicar(Origem(_sensores.ModulosRam));
        _discos.Explicar(Origem(_sensores.Discos));
        _ventoinhas.Explicar(Origem(_sensores.Ventoinhas) + VentoinhasSemLeitura());

        var estimado = _sensores.Ultimo?.WattsEstimados ?? 0;
        var total = _sensores.Ultimo?.WattsTotais ?? 0;

        TotalEstimado.Text = $"{estimado:F0} W";
        TotalAgora.Text = $"{total:F0} W";
        RotuloTomada.Text = _cfg.EficienciaFonte > 0
            ? $"Na tomada ({_cfg.EficienciaFonte:P0} de eficiência)"
            : "Na tomada";

        if (_cfg.EficienciaFonte > 0)
            TotalAgora.Text = $"{total / _cfg.EficienciaFonte:F0} W";

        Composicao.Text =
            $"RAM {_sensores.ModulosRam.Valor * _cfg.WattsPorModuloRam:0.#} + " +
            $"discos {_sensores.Discos.Valor * _cfg.WattsPorDisco:0.#} + " +
            $"ventoinhas {_sensores.Ventoinhas.Valor * _cfg.WattsPorVentoinha:0.#} + " +
            $"LEDs {_cfg.PecasComLed * _cfg.WattsPorLed:0.#} + " +
            $"bomba {(_cfg.TemWaterCooler ? _cfg.WattsBombaWaterCooler : 0):0.#} + " +
            $"placa-mãe {_cfg.WattsPlacaMae:0.#} + " +
            $"outros {_cfg.WattsOutros:0.#} W";

        Reposicionar();
    }

    private static string Origem((int Valor, bool Manual, int Detectado) q) =>
        q.Manual
            ? $"Informado à mão · o PC detectou {q.Detectado}"
            : "Detectado no seu hardware";

    /// <summary>
    /// Ventoinhas de tres pinos nao reportam rotacao, e as de controladora
    /// externa nao aparecem: por isso vale conferir a conta a olho.
    /// </summary>
    private string VentoinhasSemLeitura() =>
        _sensores.Ventoinhas.Manual ? "" : " · só conta as que reportam rotação";

    // ------------------------------------------------------------------

    private void Redetectar_Click(object sender, RoutedEventArgs e)
    {
        _cfg.ModulosRamManual = null;
        _cfg.DiscosManual = null;
        _cfg.VentoinhasManual = null;
        _cfg.Salvar();
        _sensores.AplicarConfiguracao();

        _ram.Definir(_sensores.ModulosRam.Valor);
        _discos.Definir(_sensores.Discos.Valor);
        _ventoinhas.Definir(_sensores.Ventoinhas.Valor);

        Atualizar();
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
