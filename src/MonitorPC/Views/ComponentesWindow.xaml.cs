using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class ComponentesWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;

    private LinhaDeAjuste _ram = null!;
    private LinhaDeAjuste _discos = null!;
    private LinhaDeAjuste _ventoinhas = null!;

    private LinhaDeAjuste _potenciaGpu = null!;
    private LinhaDeAjuste _potenciaCpu = null!;
    private LinhaDeAjuste _rotacaoGpu = null!;

    public ComponentesWindow(AppConfig cfg, SensorService sensores)
    {
        InitializeComponent();
        Janelas.Acompanhar(this);
        _cfg = cfg;
        _sensores = sensores;

        MontarMedidos();
        MontarQuantidades();
        MontarWatts();
        MontarEscalas();
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

        var rotuloDaMarca = new TextBlock { Text = "medido", FontSize = 11 };
        rotuloDaMarca.SetResourceReference(TextBlock.ForegroundProperty, "InfoTexto");

        var marca = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = rotuloDaMarca
        };
        marca.SetResourceReference(BackgroundProperty, "InfoFundo");
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

    /// <summary>
    /// Os tetos que viram escala das barras. Cada campo e um atalho opcional: o
    /// padrao e o app resolver sozinho, e a explicacao de cada linha diz qual
    /// fonte esta valendo neste PC neste momento.
    /// </summary>
    private void MontarEscalas()
    {
        _potenciaGpu = new LinhaDeAjuste(LinhaDeAjuste.Modo.Numero,
            "Potência máxima da GPU", "",
            _cfg.PotenciaMaximaGpuW, 0, 800,
            v => { _cfg.PotenciaMaximaGpuW = (int)v; Salvar(); }, "W");

        _potenciaCpu = new LinhaDeAjuste(LinhaDeAjuste.Modo.Numero,
            "Potência máxima da CPU", "",
            _cfg.PotenciaMaximaCpuW, 0, 500,
            v => { _cfg.PotenciaMaximaCpuW = (int)v; Salvar(); }, "W");

        _rotacaoGpu = new LinhaDeAjuste(LinhaDeAjuste.Modo.Numero,
            "Rotação máxima da ventoinha da GPU", "",
            _cfg.RotacaoMaximaGpuRpm, 0, 8000,
            v => { _cfg.RotacaoMaximaGpuRpm = (int)v; Salvar(); }, "rpm");

        Escalas.Children.Add(_potenciaGpu);
        Escalas.Children.Add(_potenciaCpu);
        Escalas.Children.Add(_rotacaoGpu);

        Escalas.Children.Add(new LinhaDeAjuste(LinhaDeAjuste.Modo.Contador,
            "Buscar catálogo atualizado",
            "Alguns kilobytes por semana, no repositório do app. 0 desliga.",
            _cfg.AtualizarCatalogoOnline ? 1 : 0, 0, 1,
            v => { _cfg.AtualizarCatalogoOnline = v >= 1; Salvar(); }));
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
        _ram.Explicar(OrigemDaQuantidade(_sensores.ModulosRam));
        _discos.Explicar(OrigemDaQuantidade(_sensores.Discos));
        _ventoinhas.Explicar(OrigemDaQuantidade(_sensores.Ventoinhas) + VentoinhasSemLeitura());

        AtualizarEscalas();

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

    /// <summary>
    /// Diz, embaixo de cada campo, qual teto esta valendo agora e de onde ele
    /// saiu. Sem isso o campo em 0 nao informa nada: o usuario nao tem como
    /// saber se o app achou a ficha tecnica da peça dele ou nao.
    /// </summary>
    private void AtualizarEscalas()
    {
        var esp = _sensores.Especificacoes;

        _potenciaGpu.Explicar(EmUso(esp.TetoDePotenciaGpu(), "W"));
        _potenciaCpu.Explicar(EmUso(esp.TetoDePotenciaCpu(), "W"));

        var chave = _sensores.Ultimo?.GpuFanChave;
        _rotacaoGpu.Explicar(chave is null
            ? "A placa de vídeo não reporta rotação de ventoinha."
            : EmUso(esp.TetoDeVentoinha(chave, _cfg.RotacaoMaximaGpuRpm), "rpm"));

        EstadoCatalogo.Text = Catalogo();
    }

    private static string EmUso(Teto teto, string unidade)
    {
        if (!teto.Existe) return "Sem escala ainda: o sensor não deu leitura.";

        var fonte = teto.Origem switch
        {
            Origem.Manual => "você informou",
            Origem.Hardware => "medido na própria peça",
            Origem.Extrapolado => "projetado pelos sensores",
            Origem.Catalogo => "ficha técnica do modelo",
            _ => "maior valor já medido"
        };

        return $"Valendo agora: {teto.Valor:F0} {unidade} · {fonte}";
    }

    /// <summary>Se o catalogo reconheceu as pecas deste PC, e de que versao ele é.</summary>
    private string Catalogo()
    {
        var esp = _sensores.Especificacoes;
        var achados = new List<string>();

        if (esp.Cpu is { } c) achados.Add($"CPU como {c.Nome}");
        if (esp.Gpu is { } g) achados.Add($"GPU como {g.Nome}");

        var versao = string.IsNullOrWhiteSpace(esp.Catalogo.Versao)
            ? "versão desconhecida"
            : $"versão {esp.Catalogo.Versao}";

        return achados.Count > 0
            ? $"Catálogo {versao}: reconheceu {string.Join(" e ", achados)}."
            : $"Catálogo {versao}: nenhuma peça deste PC está nele, então as escalas " +
              "vêm dos sensores e da medição.";
    }

    private static string OrigemDaQuantidade((int Valor, bool Manual, int Detectado) q) =>
        q.Manual
            ? $"Informado à mão · o PC detectou {q.Detectado}"
            : "Detectado no seu hardware";

    /// <summary>
    /// Ventoinhas de tres pinos nao reportam rotacao, e as de controladora
    /// externa nao aparecem: por isso vale conferir a conta a olho.
    /// </summary>
    private string VentoinhasSemLeitura() =>
        _sensores.Ventoinhas.Manual ? "" : " · só conta as que já giraram desde que o app abriu";

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

    /// <summary>
    /// Mede a rotacao maxima das ventoinhas. Fica aqui, junto das escalas, porque
    /// e disso que se trata: o numero que a barra usa como cheio.
    /// </summary>
    private void Calibrar_Click(object sender, RoutedEventArgs e)
    {
        new CalibracaoWindow(_cfg, _sensores) { Owner = this }.ShowDialog();
        Atualizar();
    }

    private void Voltar_Click(object sender, RoutedEventArgs e) => Close();

    private void Reposicionar()
    {
        Janelas.Ancorar(this, Owner);
    }

}
