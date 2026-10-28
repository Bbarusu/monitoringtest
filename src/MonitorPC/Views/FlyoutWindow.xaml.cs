using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MonitorPC.Models;
using MonitorPC.Services;

namespace MonitorPC.Views;

public partial class FlyoutWindow : Window
{
    private enum Aba { Geral, Cpu, Gpu, Disco, Energia }

    private const double Raio = 100;
    private const double RaioCpu = 69;

    /// <summary>5 minutos de historico a 1 s, que e o que o grafico mostra.</summary>
    private const int PontosDoHistorico = 300;



    private readonly AppConfig _cfg;
    private readonly SensorService _sensores;
    private readonly AlertService _alertas;
    private readonly Queue<double> _historicoCpu = new();
    private readonly Queue<double> _historicoGpu = new();
    private readonly Queue<double> _historicoDisco = new();
    private readonly Queue<double> _historicoEnergia = new();
    private readonly DiskProcessService _processos = new();

    /// <summary>Quantas linhas a lista de processos reserva, para a altura nao oscilar.</summary>
    private const int LinhasDeProcesso = 3;

    /// <summary>-1 mostra o conjunto; acima disso, o indice da unidade escolhida.</summary>
    private int _discoEscolhido = -1;

    /// <summary>Marca que ha maximo novo a gravar; o disco so e tocado ao fechar.</summary>
    private bool _maximosMudaram;

    private Aba _aba = Aba.Geral;
    private bool _trocandoPerfil;
    private DateTime _recadoAte;
    private bool _posicionando;
    private bool _montandoSeletor;
    private Alerta? _alertaNaTela;

    public FlyoutWindow(AppConfig cfg, SensorService sensores, AlertService alertas)
    {
        InitializeComponent();
        _cfg = cfg;
        _sensores = sensores;
        _alertas = alertas;

        AplicarTransparencia();
        _sensores.Atualizado += snap => Dispatcher.BeginInvoke(() => Aplicar(snap));

        // O painel e ancorado pela base: crescendo, ele sobe, em vez de avancar
        // sobre a barra de tarefas. Vale para troca de aba e para qualquer
        // conteudo que mude de altura.
        SizeChanged += (_, _) => Posicionar();

        // Por padrao o painel fica aberto mesmo sem foco, para dar para olhar,
        // trocar de janela e tirar print. Quem fecha e o botao de minimizar
        // ou o icone da bandeja.
        Deactivated += (_, _) =>
        {
            if (!_cfg.FecharAoPerderFoco) return;

            // Abrir uma tela filha tira o foco do painel: esconde-lo aqui faria
            // a tela perder a referencia de onde se posicionar.
            if (OwnedWindows.Count > 0) return;

            Esconder();
        };
    }

    // ------------------------------------------------------------------
    // Posicionamento e visibilidade

    /// <summary>
    /// Canto inferior direito da area de trabalho util, respeitando a barra de
    /// tarefas em qualquer lado da tela. Depois de arrastado, o painel fica onde
    /// o usuario deixou.
    /// </summary>
    private void Posicionar()
    {
        // UpdateLayout dispara SizeChanged, que chama este metodo de novo:
        // a guarda evita a recursao.
        if (_posicionando) return;
        _posicionando = true;

        try
        {
            UpdateLayout();

            if (_cfg.PosicaoLivre)
            {
                Left = _cfg.PosicaoX;
                Top = _cfg.PosicaoY;
            }
            else
            {
                // Sem posicao propria, o painel volta ao canto do monitor
                // principal, que e onde fica a bandeja.
                var principal = SystemParameters.WorkArea;
                Left = principal.Right - ActualWidth;
                Top = principal.Bottom - ActualHeight;
            }

            // A resolucao pode ter mudado, ou a aba ter crescido: puxa de volta,
            // respeitando o monitor em que o painel esta.
            Janelas.Conter(this);
        }
        finally
        {
            _posicionando = false;
        }
    }

    /// <summary>
    /// Arrastar de qualquer area livre do painel. Botoes e abas tratam o proprio
    /// clique, entao o evento nao chega aqui vindo deles.
    /// </summary>
    private void Painel_Arrastar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // Sai se o clique era de alguém: botão, aba, seletor, barra de
        // rolagem. Sem esta conferência, arrastar de qualquer ponto custaria
        // o painel inteiro parar de responder a clique.
        if (EhControle(e.OriginalSource as DependencyObject)) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // O botao pode ter sido solto antes de o arrasto comecar.
            return;
        }

        // So nesta sessao: a proxima abertura volta ao canto, saindo da bandeja.
        _cfg.PosicaoLivre = true;
        _cfg.PosicaoX = Left;
        _cfg.PosicaoY = Top;
    }

    /// <summary>
    /// O clique nasceu dentro de um controle que precisa dele.
    ///
    /// Sobe a arvore visual a partir de onde o clique nasceu. Sobe, e nao
    /// confere so a origem, porque o clique num botao nasce em algum
    /// <c>Border</c> ou <c>TextBlock</c> do template dele — a origem quase
    /// nunca e o controle em si.
    ///
    /// <c>ButtonBase</c> cobre botao, aba e caixa de selecao de uma vez;
    /// <c>Thumb</c> e <c>ScrollBar</c> cobrem a rolagem, que seria o pior
    /// lugar para o arrasto roubar o clique.
    /// </summary>
    private static bool EhControle(DependencyObject? origem)
    {
        while (origem is not null)
        {
            if (origem is System.Windows.Controls.Primitives.ButtonBase
                       or System.Windows.Controls.Primitives.ScrollBar
                       or System.Windows.Controls.Primitives.Thumb
                       or System.Windows.Controls.Primitives.TextBoxBase
                       or ComboBox
                       or Slider
                       or ListBoxItem
                       or MenuItem)
                return true;

            // GetParent exige um Visual: chamado com outra coisa, ele lança.
            // Um clique sempre nasce num Visual, mas o custo de garantir é
            // nenhum e o de errar é derrubar o painel.
            if (origem is not Visual) return false;

            origem = VisualTreeHelper.GetParent(origem);
        }

        return false;
    }

    /// <summary>
    /// Grava os maiores valores ja medidos. Chamado ao fechar o painel, nao a
    /// cada leitura: sao poucos bytes, mas escrever a cada segundo nao se
    /// justifica.
    /// </summary>
    public void GravarMaximos()
    {
        // As curvas aprendidas tambem entram aqui: sao o que o app sabe das
        // ventoinhas, e perde-las a cada fechamento obrigaria a reaprender tudo.
        if (_maximosMudaram) _sensores.Especificacoes.MarcarMudanca();

        _maximosMudaram = false;
        _sensores.Especificacoes.Gravar();
    }

    /// <summary>Devolve o painel ao canto, pelo menu da bandeja.</summary>
    public void VoltarAoCanto()
    {
        _cfg.PosicaoLivre = false;
        Posicionar();
    }

    public void Mostrar()
    {
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
        SincronizarPerfil();

        Opacity = 0;
        Show();
        Posicionar();
        Activate();

        _sensores.DefinirPainelAberto(true);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    public void Esconder()
    {
        _sensores.DefinirPainelAberto(false);
        GravarMaximos();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }

    public void Alternar()
    {
        if (IsVisible) Esconder();
        else Mostrar();
    }

    /// <summary>
    /// A transparencia vai no alpha do fundo, nao na opacidade do elemento:
    /// Opacity desbota tambem o texto, os aneis e os icones, e o painel inteiro
    /// fica lavado. Com alpha so o fundo deixa passar o que esta atras.
    /// </summary>
    private void AplicarTransparencia()
    {
        Painel.Opacity = 1.0;

        if (!_cfg.Transparencia)
        {
            Painel.Background = new SolidColorBrush(ThemeService.CorDeFundo);
            return;
        }

        var alpha = (byte)(Math.Clamp(_cfg.Opacidade, AppConfig.OpacidadeMinima, 100) * 255 / 100);
        var fundo = ThemeService.CorDeFundo;
        Painel.Background = new SolidColorBrush(Color.FromArgb(alpha, fundo.R, fundo.G, fundo.B));
    }

    // ------------------------------------------------------------------
    // Abas

    private void Aba_Selecionada(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        _aba = ReferenceEquals(sender, AbaCpu) ? Aba.Cpu
             : ReferenceEquals(sender, AbaGpu) ? Aba.Gpu
             : ReferenceEquals(sender, AbaDisco) ? Aba.Disco
             : ReferenceEquals(sender, AbaEnergia) ? Aba.Energia
             : Aba.Geral;

        // A medicao por processo so corre com a aba Disco aberta. Ao voltar, as
        // amostras antigas viram um pico falso, entao sao descartadas.
        _processos.Reiniciar();

        Mostrar(TopoGeral, BaseGeral, _aba == Aba.Geral);
        Mostrar(TopoCpu, BaseCpu, _aba == Aba.Cpu);
        Mostrar(TopoGpu, BaseGpu, _aba == Aba.Gpu);
        Mostrar(TopoDisco, BaseDisco, _aba == Aba.Disco);
        Mostrar(TopoEnergia, BaseEnergia, _aba == Aba.Energia);
        SeletorDisco.Visibility = _aba == Aba.Disco ? Visibility.Visible : Visibility.Collapsed;

        static void Mostrar(UIElement topo, UIElement baseP, bool visivel)
        {
            var v = visivel ? Visibility.Visible : Visibility.Collapsed;
            topo.Visibility = v;
            baseP.Visibility = v;
        }

        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);

        // A altura muda com a aba, entao o painel precisa se reancorar no canto.
        if (IsVisible) Posicionar();
    }

    private void SeletorDisco_Alterado(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _montandoSeletor) return;

        _discoEscolhido = SeletorDisco.SelectedIndex - 1;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    private void ModoDisco_Alterado(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    private void ModoGrade_Alterado(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    // ------------------------------------------------------------------
    // Dados

    private void Aplicar(Snapshot s)
    {
        MostrarAvisoDeDriver(s.SensoresDeBaixoNivel);

        MostrarAviso(_alertas.Atual);
        GuardarHistorico(s);

        switch (_aba)
        {
            case Aba.Cpu: AplicarCpu(s); break;
            case Aba.Gpu: AplicarGpu(s); break;
            case Aba.Disco: AplicarDisco(s); break;
            case Aba.Energia: AplicarEnergia(s); break;
            default: AplicarGeral(s); break;
        }

        AtualizarStatus(s);
    }

    private void GuardarHistorico(Snapshot s)
    {
        Guardar(_historicoCpu, s.CpuUso);
        Guardar(_historicoGpu, s.GpuUso);
        Guardar(_historicoDisco, s.DiscoAtividade);

        // O grafico trabalha em porcentagem, entao a potencia entra como fracao
        // da capacidade da fonte.
        if (_cfg.CapacidadeFonteW > 0)
            Guardar(_historicoEnergia, (float)(s.WattsTotais / _cfg.CapacidadeFonteW * 100));

        // Os maximos medidos ficam guardados entre sessoes. Eles sao o ultimo
        // recurso da escala, mas tambem servem de conferencia: uma peca que
        // passa do numero da ficha tecnica denuncia a ficha errada para aquele
        // exemplar, e a medicao passa a valer.
        if (s.GpuWatts is { } w && w > _cfg.PotenciaGpuMedida)
        {
            _cfg.PotenciaGpuMedida = w;
            _maximosMudaram = true;
        }

        if (s.CpuWatts is { } cw && cw > _cfg.PotenciaCpuMedida)
        {
            _cfg.PotenciaCpuMedida = cw;
            _maximosMudaram = true;
        }

        if (s.GpuFanRpm is { } r && r > _cfg.RotacaoGpuMedida)
        {
            _cfg.RotacaoGpuMedida = r;
            _maximosMudaram = true;
        }

        // As taxas de disco entram aqui, e nao na aba: a maior leitura do dia
        // pode acontecer com o painel mostrando outra coisa.
        foreach (var d in s.Discos)
            _sensores.Especificacoes.ObservarDisco(d.Nome, d.LeituraMbs, d.EscritaMbs);

        static void Guardar(Queue<double> fila, float? valor)
        {
            if (valor is not { } v) return;
            fila.Enqueue(Math.Clamp(v, 0, 100));
            while (fila.Count > PontosDoHistorico) fila.Dequeue();
        }
    }

    private void AplicarGeral(Snapshot s)
    {
        Titulo.Text = "Meu PC";
        Chips(Icone.Termometro, s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C",
              Icone.Memoria, s.RamUso is { } r ? $"{r:F0}%" : "--");

        ValorPrincipal.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0}" : "--";
        LegendaPrincipal.Text = s.CpuUso is { } uso ? $"CPU · {uso:F0}% de carga" : "CPU";

        var carga = Math.Clamp(s.CpuUso ?? 0, 0, 100);
        Anel.Data = ArcoDeCarga(carga, Raio, 110);
        ColunaCheia.Width = new GridLength(carga, GridUnitType.Star);
        ColunaVazia.Width = new GridLength(100 - carga, GridUnitType.Star);

        // Passar do limite configurado pinta o anel e o numero de laranja.
        var quente = s.CpuTemp is { } t && t >= _cfg.LimiteCpu;
        var cor = quente ? Pincel("Alerta") : Pincel("Acento");
        Anel.Stroke = cor;
        BarraCarga.Background = cor;
        // Branco fixo sumia no tema claro. "Texto" é branco no escuro e quase
        // preto no claro, que é o que este número sempre quis dizer.
        ValorPrincipal.SetResourceReference(
            TextBlock.ForegroundProperty, quente ? "AlertaTexto" : "Texto");

        // Mostra o que existe, e nao nada quando falta uma das duas. Vídeo
        // integrado não publica temperatura própria — quem mede o calor dele é
        // o sensor da CPU, no mesmo encapsulamento —, e exigir as duas leituras
        // fazia o cartão dizer "--" numa máquina onde o uso era perfeitamente
        // legível.
        CardGpu.Text = (s.GpuTemp, s.GpuUso) switch
        {
            ({ } gt, { } gu) => $"{gt:F0}° · {gu:F0}%",
            ({ } gt, null) => $"{gt:F0}°",
            (null, { } gu) => $"{gu:F0}%",
            _ => "--"
        };

        CardRam.Text = s.RamUsadaGb is { } usada && s.RamTotalGb is { } total
            ? $"{usada:F1} / {total:F0} GB"
            : s.RamUso is { } ru ? $"{ru:F0}% em uso" : "--";
    }

    private void AplicarCpu(Snapshot s)
    {
        Titulo.Text = "CPU";
        Chips(Icone.Termometro, s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "-- °C",
              Icone.Relogio, s.CpuClockMhz is > 0 ? Clock(s.CpuClockMhz!.Value) : "-- GHz");

        var uso = Math.Clamp(s.CpuUso ?? 0, 0, 100);
        CpuUsoTexto.Text = s.CpuUso is null ? "--" : $"{uso:F0}";
        AnelCpu.Data = ArcoDeCarga(uso, RaioCpu, 75);

        var quente = s.CpuTemp is { } t && t >= _cfg.LimiteCpu;
        AnelCpu.Stroke = quente ? Pincel("Alerta") : Pincel("Acento");

        CpuTempTexto.Text = s.CpuTemp is > 0 ? $"{s.CpuTemp:F0} °C" : "n/d";
        CpuTempTexto.Foreground = quente ? Pincel("AlertaTexto") : Pincel("Texto");
        CpuClockTexto.Text = s.CpuClockMhz is > 0 ? Clock(s.CpuClockMhz!.Value) : "n/d";
        CpuWattsTexto.Text = s.CpuWatts is > 0 ? $"{s.CpuWatts:F0} W" : "n/d";
        CpuTensaoTexto.Text = s.CpuTensao is > 0 ? $"{s.CpuTensao:0.00} V" : "n/d";

        // Os quadrados sao pequenos demais para carregar "66 / 88 W" sem
        // atropelar em um processador de tres digitos, entao o teto e a
        // procedencia dele ficam no tooltip. Sem isso, 66 W nao diz se o
        // processador esta folgado ou no limite.
        var esp = _sensores.Especificacoes;

        CpuWattsTexto.ToolTip = Contexto(s.CpuWatts, esp.TetoDePotenciaCpu(), "W");
        CpuTempTexto.ToolTip = Contexto(s.CpuTemp, esp.TetoTermicoCpu(_cfg.LimiteCpu), "°C");
        CpuClockTexto.ToolTip = Contexto(s.CpuClockMhz,
            esp.TetoDeClockCpu(s.CpuClockMhz ?? 0), "MHz");

        DesenharHistorico();
        MontarGrade(s);
        MontarBarrasCpu(s);
    }

    private void AplicarGpu(Snapshot s)
    {
        Titulo.Text = s.GpuNome ?? "GPU";
        Chips(Icone.Termometro, s.GpuTemp is > 0 ? $"{s.GpuTemp:F0} °C" : "-- °C",
              Icone.Raio, s.GpuWatts is > 0 ? $"{s.GpuWatts:F0} W" : "-- W");

        var uso = Math.Clamp(s.GpuUso ?? 0, 0, 100);
        GpuUsoTexto.Text = s.GpuUso is null ? "--" : $"{uso:F0}";
        AnelGpu.Data = ArcoDeCarga(uso, RaioCpu, 75);

        var quente = s.GpuTemp is { } t && t >= _cfg.LimiteGpu;
        AnelGpu.Stroke = quente ? Pincel("Alerta") : Pincel("Acento");

        GpuTempTexto.Text = s.GpuTemp is > 0 ? $"{s.GpuTemp:F0} °C" : "n/d";
        GpuTempTexto.Foreground = quente ? Pincel("AlertaTexto") : Pincel("Texto");
        GpuClockTexto.Text = s.GpuClockNucleo is > 0 ? Clock(s.GpuClockNucleo!.Value) : "n/d";
        GpuClockMemTexto.Text = s.GpuClockMemoria is > 0 ? Clock(s.GpuClockMemoria!.Value) : "n/d";

        var esp = _sensores.Especificacoes;
        GpuTempTexto.ToolTip = Contexto(s.GpuTemp, esp.TetoTermicoGpu(_cfg.LimiteGpu), "°C");
        GpuClockTexto.ToolTip = Contexto(s.GpuClockNucleo,
            esp.TetoDeClockGpu(s.GpuClockNucleo ?? 0), "MHz");

        AplicarHotSpot(s);
        DesenharHistoricoGpu();
        MontarBarrasGpu(s);
    }

    /// <summary>
    /// O hot spot fica naturalmente acima da borda: nas RDNA3 uma diferenca de
    /// 15 a 25 °C sob carga e esperada. So passa a sugerir contato ruim do
    /// dissipador acima disso, ou quando o proprio hot spot encosta no teto de
    /// 110 °C — e nenhum dos dois significa muita coisa com a GPU ociosa, quando
    /// os dois sensores quase se encontram.
    /// </summary>
    private void AplicarHotSpot(Snapshot s)
    {
        if (s.GpuHotSpot is not { } hot || hot <= 0)
        {
            GpuHotTexto.Text = "n/d";
            GpuHotTexto.Foreground = Pincel("Texto");
            AvisoHotSpot.Visibility = Visibility.Collapsed;
            return;
        }

        var delta = s.GpuTemp is { } borda && borda > 0 ? hot - borda : (float?)null;
        // So vale julgar sob carga: parado, a diferenca some sozinha.
        var sobCarga = s.GpuUso is > 30;
        var preocupante = sobCarga && (delta >= 30 || hot >= 100);

        GpuHotTexto.Text = delta is { } d ? $"{hot:F0} °C  Δ{d:F0}" : $"{hot:F0} °C";
        GpuHotTexto.Foreground = preocupante ? Pincel("AlertaTexto") : Pincel("Texto");

        AvisoHotSpot.Visibility = preocupante ? Visibility.Visible : Visibility.Collapsed;
        if (preocupante)
            TextoHotSpot.Text = hot >= 100
                ? $"Hot spot em {hot:F0} °C. Perto do teto de 110 °C: vale olhar o " +
                  "fluxo de ar do gabinete."
                : $"Hot spot {delta:F0} °C acima da borda sob carga. Acima de 30 °C de " +
                  "diferença, costuma ser contato irregular do dissipador.";
    }

    /// <summary>
    /// Cada barra mede o valor atual contra o maximo que aquela peca aguenta, e
    /// nao contra o maximo que ela ja fez. O teto vem da melhor fonte
    /// disponivel — a peca, os sensores combinados, a ficha tecnica — e o
    /// tooltip diz qual foi, porque uma barra pela metade so informa alguma
    /// coisa se der para saber metade do que.
    /// </summary>
    /// <summary>
    /// Transforma uma leitura solta em leitura com referencia: quanto e, de
    /// quanto, e quem disse que o teto e esse. Devolve nulo quando nao ha nada a
    /// acrescentar, para o quadrado nao ganhar um tooltip vazio.
    /// </summary>
    private static string? Contexto(float? valor, Teto teto, string unidade)
    {
        if (valor is not { } v || v <= 0) return null;
        if (!teto.Existe) return teto.Explicacao(unidade);

        return $"{v:F0} de {teto.Valor:F0} {unidade} · {teto.Porcentagem(v):F0}%\n" +
               teto.Explicacao(unidade);
    }

    private void MontarBarrasGpu(Snapshot s)
    {
        var esp = _sensores.Especificacoes;
        var barras = new List<(string Rotulo, string Valor, double Pct, string? Dica)>();

        if (s.VramUsadaMb is { } usada && s.VramTotalMb is { } total && total > 0)
            barras.Add((s.VramCompartilhada ? "Memória gráfica" : "VRAM",
                $"{usada / 1024:0.0} / {total / 1024:0.0} GB",
                usada / total * 100,
                s.VramCompartilhada
                    ? "Vídeo integrado não tem memória própria: isto é RAM do " +
                      "sistema cedida à GPU. Escala: o total que o Windows reserva."
                    : "Escala: total informado pela própria placa."));

        if (s.GpuWatts is { } w && w > 0)
        {
            var teto = esp.TetoDePotenciaGpu();

            barras.Add(("Potência",
                teto.Existe ? $"{w:F0} / {teto.Valor:F0} W" : $"{w:F0} W",
                teto.Porcentagem(w),
                teto.Explicacao("W")));
        }

        if (s.GpuFanRpm is { } rpm)
        {
            var teto = s.GpuFanChave is { } chave
                ? esp.TetoDeVentoinha(chave, _cfg.RotacaoMaximaGpuRpm)
                : Teto.Nenhum;

            var valor = rpm <= 0
                ? "parado"
                : teto.Existe ? $"{rpm:F0} / {teto.Valor:F0} rpm" : $"{rpm:F0} rpm";

            var dica = teto.Explicacao("rpm");
            if (s.GpuFanPwm is { } pwm)
                dica += $"\nPWM agora: {pwm:F0}%.";

            barras.Add(("Ventilador", valor, teto.Porcentagem(rpm), dica));
        }

        if (s.GpuTempMemoria is { } tm && tm > 0)
            barras.Add(("Temp. memória", $"{tm:F0} °C", Math.Clamp(tm / 110 * 100, 0, 100),
                "Escala até 110 °C, o limite típico de GDDR. A memória não publica " +
                "limiar próprio, ao contrário dos discos."));

        Desenhar(BarrasGpu, barras);
    }

    /// <summary>
    /// Reaproveita as barras existentes em vez de recriar a lista: as abas se
    /// redesenham a cada leitura, e recriar faria o tooltip fechar na mao do
    /// usuario toda vez.
    /// </summary>
    private static void Desenhar(
        System.Windows.Controls.Panel alvo,
        List<(string Rotulo, string Valor, double Pct, string? Dica)> barras)
    {
        while (alvo.Children.Count > barras.Count)
            alvo.Children.RemoveAt(alvo.Children.Count - 1);
        while (alvo.Children.Count < barras.Count)
            alvo.Children.Add(new BarraDeMetrica());

        for (var i = 0; i < barras.Count; i++)
        {
            var (rotulo, valor, pct, dica) = barras[i];
            ((BarraDeMetrica)alvo.Children[i]).Atualizar(rotulo, valor, pct, dica);
        }
    }

    /// <summary>
    /// As barras da CPU, pela mesma régua da GPU: potência contra o PPT do
    /// modelo, temperatura contra o TjMax, e cada ventoinha da placa-mãe contra
    /// a rotação máxima que ela própria revelou.
    ///
    /// As ventoinhas do gabinete só existiam na contagem da estimativa de
    /// energia — a rotação era lida e descartada. Agora que há teto para elas,
    /// passam a caber numa barra.
    /// </summary>
    private void MontarBarrasCpu(Snapshot s)
    {
        var esp = _sensores.Especificacoes;
        var barras = new List<(string Rotulo, string Valor, double Pct, string? Dica)>();

        if (s.CpuWatts is { } w && w > 0)
        {
            var teto = esp.TetoDePotenciaCpu();
            barras.Add(("Potência",
                teto.Existe ? $"{w:F0} / {teto.Valor:F0} W" : $"{w:F0} W",
                teto.Porcentagem(w),
                teto.Explicacao("W")));
        }

        if (s.CpuTemp is { } t && t > 0)
        {
            var teto = esp.TetoTermicoCpu(_cfg.LimiteCpu);
            barras.Add(("Temperatura",
                teto.Existe ? $"{t:F0} / {teto.Valor:F0} °C" : $"{t:F0} °C",
                teto.Porcentagem(t),
                teto.Explicacao("°C")));
        }

        // Só as ventoinhas de verdade chegam aqui: o serviço só reconhece um
        // canal depois de vê-lo girar, então conector vazio não entra. Uma
        // ventoinha parada continua aparecendo, como "parada" — some-la faria a
        // aba pular de altura toda vez que ela desligasse.
        foreach (var (nome, rpm) in s.Ventoinhas.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (!_cfg.MostrarVentoinhasNaCpu) break;

            var chave = s.ChavesDeVentoinha.GetValueOrDefault(nome, nome);

            // A escolha de quais ver fica nos Ajustes. A leitura continua
            // acontecendo para todas: é ela que alimenta as curvas de PWM.
            if (_cfg.VentoinhasOcultas.Contains(chave)) continue;

            var teto = esp.TetoDeVentoinha(chave);

            var dica = teto.Explicacao("rpm");
            if (s.ControlesDeVentoinha.TryGetValue(nome, out var pwm))
                dica += $"\nPWM agora: {pwm:F0}%.";

            var valor = rpm <= 0
                ? "parada"
                : teto.Existe ? $"{rpm:F0} / {teto.Valor:F0} rpm" : $"{rpm:F0} rpm";

            barras.Add((nome, valor, teto.Porcentagem(rpm), dica));
        }

        // Sem barra nenhuma — um PC sem sensor de potência e com as ventoinhas
        // desligadas na configuração —, o rótulo da seção ficaria sozinho.
        RotuloBarrasCpu.Visibility = barras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Desenhar(BarrasCpu, barras);
    }

    /// <summary>
    /// Leitura e escrita contra o maior valor que a unidade já atingiu, e
    /// temperatura contra o limiar que ela própria publica pelo SMART.
    /// </summary>
    private void MontarBarrasDisco(Snapshot s, DiscoInfo? escolhido)
    {
        var esp = _sensores.Especificacoes;
        var barras = new List<(string Rotulo, string Valor, double Pct, string? Dica)>();

        var disco = escolhido ?? s.Discos.FirstOrDefault();

        if (disco is not null)
        {
            var leitura = disco.LeituraMbs ?? 0;
            var escrita = disco.EscritaMbs ?? 0;

            var tetoLeitura = esp.TetoDeLeitura(disco.Nome);
            barras.Add(("Leitura",
                tetoLeitura.Existe ? $"{Taxa(leitura)} / {Taxa((float)tetoLeitura.Valor)}" : Taxa(leitura),
                tetoLeitura.Porcentagem(leitura),
                tetoLeitura.Explicacao("MB/s")));

            var tetoEscrita = esp.TetoDeEscrita(disco.Nome);
            barras.Add(("Escrita",
                tetoEscrita.Existe ? $"{Taxa(escrita)} / {Taxa((float)tetoEscrita.Valor)}" : Taxa(escrita),
                tetoEscrita.Porcentagem(escrita),
                tetoEscrita.Explicacao("MB/s")));

            if (disco.TemperaturaC is { } t && t > 0)
            {
                var teto = esp.TetoDeDisco(disco.CriticoC, disco.AvisoC, _cfg.LimiteDisco);
                barras.Add(("Temperatura",
                    teto.Existe ? $"{t:F0} / {teto.Valor:F0} °C" : $"{t:F0} °C",
                    teto.Porcentagem(t),
                    teto.Explicacao("°C")));
            }
        }

        Desenhar(BarrasDisco, barras);
    }

    private void AplicarDisco(Snapshot s)
    {
        MontarSeletor(s);

        // -1 e o conjunto; acima disso, uma unidade so.
        var escolhido = _discoEscolhido >= 0 && _discoEscolhido < s.Discos.Count
            ? s.Discos[_discoEscolhido]
            : null;

        var atividade = Math.Clamp(escolhido?.Atividade ?? s.DiscoAtividade ?? 0, 0, 100);
        // Com uma unidade escolhida, a temperatura e dela. No conjunto, a da
        // primeira unidade — a do sistema —, nao a mais alta das tres.
        var temperatura = escolhido?.TemperaturaC
                       ?? s.Discos.FirstOrDefault()?.TemperaturaC
                       ?? s.DiscoTempMaior;
        var vida = escolhido?.VidaPct ?? s.DiscoVidaMenor;
        var leitura = escolhido?.LeituraMbs ?? s.DiscoLeituraMbs;
        var escrita = escolhido?.EscritaMbs ?? s.DiscoEscritaMbs;

        Titulo.Text = escolhido is not null
            ? "Disco"
            : s.Discos.Count == 1 ? "Disco" : $"Discos · {s.Discos.Count} unidades";

        Chips(Icone.Termometro, temperatura is > 0 ? $"{temperatura:F0} °C" : "-- °C",
              Icone.Escudo, vida is { } v ? $"Vida {v:F0}%" : "Vida --");

        DiscoUsoTexto.Text = escolhido?.Atividade is null && s.DiscoAtividade is null
            ? "--"
            : $"{atividade:F0}";
        AnelDisco.Data = ArcoDeCarga(atividade, RaioCpu, 75);

        // O alerta olha a mais alta entre as unidades: um disco quente nao pode
        // passar despercebido so porque o painel mostra outro.
        var maisAlta = escolhido?.TemperaturaC ?? s.DiscoTempMaior;
        var quente = maisAlta is { } t && t >= _cfg.LimiteDisco;
        AnelDisco.Stroke = quente ? Pincel("Alerta") : Pincel("Acento");

        DiscoLeituraTexto.Text = Taxa(leitura);
        DiscoEscritaTexto.Text = Taxa(escrita);
        DiscoTempTexto.Text = temperatura is > 0 ? $"{temperatura:F0} °C" : "n/d";
        DiscoTempTexto.Foreground = quente ? Pincel("AlertaTexto") : Pincel("Texto");
        DiscoTempTexto.ToolTip = DicaDeTemperatura(s, escolhido);

        // Numa unidade so, as horas ligado cabem ao lado da vida util.
        DiscoVidaTexto.Text = escolhido is { VidaPct: { } vp, HorasLigado: { } h }
            ? $"{vp:F0}% · {h:N0} h"
            : vida is { } vm ? $"{vm:F0}%" : "n/d";

        // O historico e sempre do conjunto: trocar de unidade nao apaga o grafico.
        DesenharHistoricoDisco();
        MontarBarrasDisco(s, escolhido);

        if (ModoUnidades.IsChecked == true) MontarUnidades(s);
        else MontarProcessos();
    }

    /// <summary>
    /// Os limiares que a propria unidade publica pelo SMART, ao lado da leitura.
    /// O alerta do painel continua no limite que voce configurou: o limiar do
    /// fabricante marca onde o disco comeca a reduzir desempenho, perto de 85 °C,
    /// o que como aviso chegaria tarde demais.
    /// </summary>
    private string? DicaDeTemperatura(Snapshot s, DiscoInfo? escolhido)
    {
        var linhas = new List<string>();

        if (escolhido is null && s.Discos.Count > 1)
            linhas.AddRange(s.Discos
                .Where(d => d.TemperaturaC is > 0)
                .Select(d => $"{d.Nome}: {d.TemperaturaC:F0} °C"));

        var disco = escolhido ?? s.Discos.FirstOrDefault();
        if (disco is not null)
        {
            var teto = _sensores.Especificacoes.TetoDeDisco(
                disco.CriticoC, disco.AvisoC, _cfg.LimiteDisco);

            if (teto.Origem == Origem.Hardware)
            {
                if (linhas.Count > 0) linhas.Add("");
                linhas.Add(disco.AvisoC is { } a && disco.CriticoC is { } c
                    ? $"{Encurtar(disco.Nome)} avisa a partir de {a:F0} °C e marca " +
                      $"crítico em {c:F0} °C, segundo o próprio SMART."
                    : $"{Encurtar(disco.Nome)} publica {teto.Valor:F0} °C como limiar.");
                linhas.Add($"Seu alerta está em {_cfg.LimiteDisco:F0} °C.");
            }
        }

        return linhas.Count > 0 ? string.Join("\n", linhas) : null;
    }

    /// <summary>
    /// A lista do seletor so e refeita quando as unidades mudam, para nao
    /// descartar a escolha do usuario a cada leitura.
    /// </summary>
    private void MontarSeletor(Snapshot s)
    {
        var desejado = new List<string> { s.Discos.Count == 1 ? "a unidade" : "todas" };
        desejado.AddRange(s.Discos.Select(d => Encurtar(d.Nome)));

        var atual = SeletorDisco.Items.Cast<object>().Select(x => x?.ToString()).ToList();
        if (atual.SequenceEqual(desejado)) return;

        _montandoSeletor = true;
        try
        {
            SeletorDisco.Items.Clear();
            foreach (var item in desejado) SeletorDisco.Items.Add(item);

            if (_discoEscolhido >= s.Discos.Count) _discoEscolhido = -1;
            SeletorDisco.SelectedIndex = _discoEscolhido + 1;
        }
        finally
        {
            _montandoSeletor = false;
        }
    }

    /// <summary>Nomes de modelo sao longos demais para o cabecalho.</summary>
    private static string Encurtar(string nome) =>
        nome.Length <= 22 ? nome : nome[..21].TrimEnd() + "…";

    /// <summary>
    /// As taxas de disco em repouso ficam na casa dos KB/s. Arredondar tudo para
    /// MB/s escondia o movimento real, entao a unidade acompanha a grandeza.
    /// </summary>
    private static string Taxa(float? mbs)
    {
        if (mbs is not { } v) return "n/d";
        if (v <= 0) return "0";
        return v < 1 ? $"{v * 1024:0} KB/s" : $"{v:0.0} MB/s";
    }

    /// <summary>Uma linha por unidade, com o que o SMART informa.</summary>
    private void MontarUnidades(Snapshot s)
    {
        RotuloListaDisco.Text = "Unidades";

        SincronizarLinhas(s.Discos.Count);

        for (var i = 0; i < s.Discos.Count; i++)
        {
            var d = s.Discos[i];

            var detalhe = new List<string>();
            if (d.TemperaturaC is > 0) detalhe.Add($"{d.TemperaturaC:F0} °C");
            if (d.VidaPct is { } vida) detalhe.Add($"vida {vida:F0}%");
            if (d.HorasLigado is { } horas) detalhe.Add($"{horas:N0} h");

            var espaco = d.TotalGb is { } total && total > 0 && d.UsadoPct is { } usado
                ? $"{total * usado / 100:N0} / {total:N0} GB"
                : d.TotalGb is { } t ? $"{t:N0} GB" : "";

            ((LinhaDeDisco)ListaDisco.Children[i]).Atualizar(
                d.Nome,
                string.Join(" · ", detalhe),
                espaco,
                d.UsadoPct ?? 0);
        }
    }

    /// <summary>
    /// Os processos que mais usam disco agora. A medicao so corre com esta aba
    /// aberta, para nao varrer a lista de processos o tempo todo.
    ///
    /// As linhas sao sempre as mesmas: com a quantidade variando, o painel
    /// mudava de altura a cada leitura.
    /// </summary>
    /// <summary>
    /// Fila de uma medicao por vez. Sem isto, uma varredura que demorasse mais
    /// que o intervalo de leitura empilharia varreduras, cada uma roubando a
    /// amostra anterior da outra e estragando as taxas.
    /// </summary>
    private bool _medindoProcessos;

    private async void MontarProcessos()
    {
        RotuloListaDisco.Text = "Consumindo o disco agora";

        if (_medindoProcessos) return;
        _medindoProcessos = true;

        IReadOnlyList<ProcessoDisco> processos;

        // Fora da thread da interface: a varredura abre um handle por processo,
        // e são trezentos. Mesmo rápida, não pode acontecer entre dois quadros.
        try
        {
            processos = await Task.Run(() => _processos.Medir(LinhasDeProcesso));
        }
        catch (Exception ex)
        {
            Log.Erro("Medir processos de disco", ex);
            return;
        }
        finally
        {
            _medindoProcessos = false;
        }

        // A aba pode ter mudado durante a medição.
        if (_aba != Aba.Disco || ModoUnidades.IsChecked == true) return;

        var maior = processos.Count > 0 ? processos.Max(p => p.TotalMbs) : 0;

        SincronizarLinhas(LinhasDeProcesso);

        for (var i = 0; i < LinhasDeProcesso; i++)
        {
            var linha = (LinhaDeDisco)ListaDisco.Children[i];

            if (i >= processos.Count)
            {
                linha.Pid = null;
                linha.Cursor = null;
                linha.ToolTip = null;
                linha.Vazia(i == 0 ? "Nenhum processo lendo ou escrevendo" : "");
                continue;
            }

            var p = processos[i];
            linha.Pid = p.Pid;
            linha.NomeDoProcesso = p.Nome;
            linha.Cursor = Cursors.Hand;
            linha.ToolTip = "Clique para ver os arquivos abertos";
            linha.Atualizar(
                p.Nome,
                $"PID {p.Pid} · leitura {p.LeituraMbs:0.0} · escrita {p.EscritaMbs:0.0} MB/s",
                $"{p.TotalMbs:0.0} MB/s",
                maior > 0 ? p.TotalMbs / maior * 100 : 0);
        }
    }

    private void SincronizarLinhas(int quantidade)
    {
        while (ListaDisco.Children.Count > quantidade)
            ListaDisco.Children.RemoveAt(ListaDisco.Children.Count - 1);
        while (ListaDisco.Children.Count < quantidade)
        {
            var linha = new LinhaDeDisco();
            linha.MouseLeftButtonUp += Processo_Clicado;
            ListaDisco.Children.Add(linha);
        }
    }

    private void AplicarEnergia(Snapshot s)
    {
        Titulo.Text = "Energia";
        Chips(Icone.Raio, $"{s.WattsTotais:F0} W",
              Icone.Relogio, $"{Energia(s.WhAcumulado)} na sessão");

        EnergiaWattsTexto.Text = $"{s.WattsTotais:F0}";

        // O anel compara a potencia com a capacidade da fonte.
        var fracao = _cfg.CapacidadeFonteW > 0
            ? s.WattsTotais / _cfg.CapacidadeFonteW * 100
            : 0;
        AnelEnergia.Data = ArcoDeCarga(fracao, RaioCpu, 75);
        EnergiaLegenda.Text = _cfg.CapacidadeFonteW > 0
            ? $"de {_cfg.CapacidadeFonteW} W"
            : "Potência agora";

        EnergiaSessaoTexto.Text = Energia(s.WhAcumulado);
        EnergiaHojeTexto.Text = Energia(s.WhDeHoje);

        LegendaDoConsumo.Text =
            "Estimativa, e só com o app aberto: CPU e placa de vídeo vêm de sensor, " +
            "o resto é a conta montada em Componentes. O tempo com o app fechado não " +
            "entra. O registro por dia fica guardado em arquivo.";
        EnergiaMediaTexto.Text = $"{s.WattsMedia:F0} W";
        EnergiaPicoTexto.Text = $"{s.WattsPico:F0} W";
        EnergiaTomadaTexto.Text = _cfg.EficienciaFonte > 0
            ? $"{s.WattsTotais / _cfg.EficienciaFonte:F0} W"
            : "n/d";

        DesenharHistoricoEnergia();
        MontarDistribuicao(s);
    }

    /// <summary>
    /// Separa o que e medido do que e estimado: so CPU e GPU tem sensor de
    /// potencia; o resto vem das quantidades detectadas e dos watts por unidade.
    /// </summary>
    private void MontarDistribuicao(Snapshot s)
    {
        var gpu = s.GpuWatts ?? 0;
        var cpu = s.CpuWatts ?? 0;
        var outros = s.WattsEstimados;
        var total = Math.Max(gpu + cpu + outros, 0.001);

        FaixaGpu.Width = new GridLength(gpu / total * 100, GridUnitType.Star);
        FaixaCpu.Width = new GridLength(cpu / total * 100, GridUnitType.Star);
        FaixaOutros.Width = new GridLength(outros / total * 100, GridUnitType.Star);

        var itens = new (string Cor, string Nome, double Watts, string Origem)[]
        {
            ("Acento", "GPU", gpu, "medido"),
            ("SerieMedida", "CPU", cpu, "medido"),
            ("SerieEstimada", DescricaoDosOutros(s), outros, "estimado")
        };

        while (LegendaEnergia.Children.Count > itens.Length)
            LegendaEnergia.Children.RemoveAt(LegendaEnergia.Children.Count - 1);
        while (LegendaEnergia.Children.Count < itens.Length)
            LegendaEnergia.Children.Add(new LinhaDeLegenda());

        for (var i = 0; i < itens.Length; i++)
        {
            var (cor, nome, watts, origem) = itens[i];
            ((LinhaDeLegenda)LegendaEnergia.Children[i])
                .Atualizar(cor, nome, $"{watts:F0} W", origem);
        }

        NotaEnergia.Text = "Energia (Wh) = potência × tempo, somada a cada leitura. " +
                           "Ajuste as quantidades e os watts por unidade no config.json.";
    }

    /// <summary>
    /// Watt-hora enquanto o número é pequeno, kWh depois.
    ///
    /// Antes era sempre kWh com duas casas, e isso fazia o contador parecer
    /// travado: a 90 W, um PC leva quase sete minutos para andar de 0,01 para
    /// 0,02 kWh, e nesse meio tempo o número não mexia. Em Wh com uma casa ele
    /// anda a cada poucos segundos, que é o que se espera de um contador.
    /// O kWh volta a partir de 1 kWh, que é a unidade da conta de luz.
    /// </summary>
    private static string Energia(double wh) =>
        wh < 1000 ? $"{wh:0.0} Wh" : $"{wh / 1000.0:0.00} kWh";

    private static string DescricaoDosOutros(Snapshot s)
    {
        var partes = new List<string>();
        if (s.ModulosRam > 0)
            partes.Add($"{s.ModulosRam} {(s.ModulosRam == 1 ? "pente" : "pentes")} de RAM");
        if (s.Discos2 > 0) partes.Add($"{s.Discos2} discos");
        if (s.VentoinhasAtivas > 0) partes.Add($"{s.VentoinhasAtivas} ventoinhas");

        return partes.Count == 0 ? "Outros componentes" : string.Join(", ", partes) + " e outros";
    }

    /// <summary>
    /// O aviso fica acima das abas, entao vale em qualquer uma: uma temperatura
    /// alta nao deve depender de o usuario estar na aba certa para aparecer.
    /// </summary>
    private void MostrarAviso(Alerta? alerta)
    {
        _alertaNaTela = alerta;

        if (alerta is null)
        {
            AvisoTemperatura.Visibility = Visibility.Collapsed;
            return;
        }

        AvisoTitulo.Text = alerta.Titulo;
        AvisoDetalhe.Text = alerta.Detalhe;

        // O botao de Economico so aparece se a pessoa pediu essa sugestao, e nao
        // faz sentido quando o plano ja e esse.
        var oferecer = _cfg.SugerirEconomico && PowerService.Atual() != PerfilEnergia.Economico;
        BotaoEconomico.Visibility = oferecer ? Visibility.Visible : Visibility.Collapsed;

        AvisoTemperatura.Visibility = Visibility.Visible;
    }

    private void AvisoEconomico_Click(object sender, RoutedEventArgs e)
    {
        if (PowerService.Aplicar(PerfilEnergia.Economico))
        {
            Recado("Plano de energia: Econômico");
            SincronizarPerfil();
            BotaoEconomico.Visibility = Visibility.Collapsed;
            return;
        }

        Recado("Não foi possível trocar o plano de energia");
    }

    /// <summary>
    /// Silencia so o componente do aviso em tela: silenciar o disco nao pode
    /// esconder um aviso da CPU, que e outro problema.
    /// </summary>
    private void AvisoSilenciar_Click(object sender, RoutedEventArgs e)
    {
        if (_alertaNaTela is not { } alerta) return;

        _alertas.Silenciar(alerta.Componente);
        AvisoTemperatura.Visibility = Visibility.Collapsed;
        Recado($"Aviso de {alerta.Componente} silenciado por 10 minutos");

        // Outro componente pode estar acima do limite e assumir o aviso agora.
        if (_sensores.Ultimo is { } ultimo)
        {
            _alertas.Avaliar(ultimo);
            MostrarAviso(_alertas.Atual);
        }
    }

    /// <summary>
    /// A faixa de aviso dos sensores de baixo nivel.
    ///
    /// O texto e montado pelo <see cref="Diagnostico"/> e nao fica mais fixo no
    /// XAML: ele dizia "abra como administrador" tambem para quem ja estava como
    /// administrador, o que mandava o usuario repetir uma tentativa que acabara
    /// de falhar e escondia o motivo de verdade.
    /// </summary>
    private void MostrarAvisoDeDriver(bool temSensores)
    {
        AvisoDriver.Visibility = temSensores ? Visibility.Collapsed : Visibility.Visible;

        if (temSensores) return;

        // Remontar o mesmo texto a cada leitura faria a faixa piscar debaixo de
        // quem esta lendo. Nada aqui muda durante a sessao.
        if (!_avisoDeDriverMontado)
        {
            AvisoDriverTexto.Text = Diagnostico.PorQueFaltamSensores();

            // O botao aparece sempre que falta sensor, inclusive sem elevacao.
            // Antes ele exigia estar elevado, com o argumento de que elevar
            // resolveria o primeiro caso — mas e justamente a maquina que nao
            // elevou a que precisa contar por que nao elevou, e sem o botao ela
            // ficava sem nenhuma forma de dizer isso.
            BotaoDiagnostico.Visibility = Visibility.Visible;

            _avisoDeDriverMontado = true;
        }
    }

    private bool _avisoDeDriverMontado;

    private void Diagnostico_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Diagnostico.Revelar(_sensores.SalvarDiagnostico());

            Recado("Relatório salvo. A pasta foi aberta com o arquivo selecionado.");
        }
        catch (Exception ex)
        {
            Log.Erro("Salvar diagnóstico", ex);
            Recado("Não foi possível salvar o relatório. O motivo ficou no erro.log.");
        }
    }

    // ------------------------------------------------------------------
    // Atualização

    private readonly AtualizacaoService _atualizacoes = new();
    private Manifesto? _atualizacaoOferecida;
    private CancellationTokenSource? _cancelarAtualizacao;

    /// <summary>
    /// Mostra a faixa de versao nova. Chamada pelo App, que e quem procura.
    ///
    /// Oferece, nao instala: baixar e executar fecha o app no meio e custa uma
    /// confirmacao do Windows, e nenhuma das duas coisas se faz nas costas de
    /// quem esta usando o PC.
    /// </summary>
    public void OferecerAtualizacao(Manifesto manifesto)
    {
        _atualizacaoOferecida = manifesto;

        AtualizacaoTitulo.Text = $"Versão {manifesto.Versao} disponível";

        MontarNovidades(manifesto.Notas);

        AtualizacaoEstado.Visibility = Visibility.Collapsed;
        AvisoAtualizacao.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// As notas da versao viram uma lista, uma novidade por linha.
    ///
    /// O arquivo de notas e escrito em linhas curtas justamente para isso:
    /// despejado como paragrafo unico, dentro de um painel de 432 px, vira um
    /// bloco que ninguem le. Linhas em branco separam itens e sao descartadas.
    /// </summary>
    private void MontarNovidades(string? notas)
    {
        ListaDeNovidades.Children.Clear();

        var linhas = (notas ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (linhas.Count == 0)
        {
            CaixaDeNovidades.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var linha in linhas)
        {
            var item = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ponto = new TextBlock
            {
                Text = "•",
                FontSize = 11,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            ponto.SetResourceReference(TextBlock.ForegroundProperty, "Acento");

            // Remove o traço de quem escreveu a nota já em lista: o marcador
            // é desenhado aqui, e dois seguidos ficariam feios.
            var texto = new TextBlock
            {
                Text = linha.TrimStart('-', '*', '•', ' '),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 15
            };
            texto.SetResourceReference(TextBlock.ForegroundProperty, "Texto");

            Grid.SetColumn(ponto, 0);
            Grid.SetColumn(texto, 1);
            item.Children.Add(ponto);
            item.Children.Add(texto);

            ListaDeNovidades.Children.Add(item);
        }

        CaixaDeNovidades.Visibility = Visibility.Visible;
    }

    private async void Atualizar_Click(object sender, RoutedEventArgs e)
    {
        if (_atualizacaoOferecida is not { } manifesto) return;

        if (_cancelarAtualizacao is not null)
        {
            _cancelarAtualizacao.Cancel();
            return;
        }

        var cancelamento = new CancellationTokenSource();
        _cancelarAtualizacao = cancelamento;

        BotaoAtualizar.Content = "Cancelar";
        BotaoAdiarAtualizacao.IsEnabled = false;

        // O estado vai na sua própria linha, e não no subtítulo: o subtítulo diz
        // qual versão você tem, e isso continua verdade durante o download.
        void Estado(string texto)
        {
            AtualizacaoEstado.Text = texto;
            AtualizacaoEstado.Visibility = Visibility.Visible;
        }

        var progresso = new Progress<double>(fracao =>
            Estado($"Baixando… {fracao * 100:F0}%"));

        try
        {
            string? recusa = null;

            var arquivo = await _atualizacoes.BaixarAsync(
                manifesto, progresso, cancelamento.Token, motivo => recusa = motivo);

            if (cancelamento.IsCancellationRequested)
            {
                Estado("Download cancelado.");
                return;
            }

            if (arquivo is null)
            {
                Estado(recusa ?? "Não foi possível baixar a atualização.");
                return;
            }

            // O aviso do Windows é dito aqui, e não na faixa: é agora que ele
            // vai aparecer. Quem vê "editor desconhecido" sem ter sido avisado
            // conclui, com razão, que pegou algo errado — e quem foi avisado
            // dois minutos antes, enquanto lia as novidades, já esqueceu.
            Estado("Arquivo conferido. Abrindo o instalador — o Windows vai " +
                   "mostrar um aviso de editor desconhecido, que é esperado.");

            if (!AtualizacaoService.Executar(arquivo))
                Estado("O instalador foi baixado e conferido, mas não abriu. " +
                       "Ele está na pasta temporária.");
        }
        finally
        {
            cancelamento.Dispose();
            _cancelarAtualizacao = null;

            BotaoAtualizar.Content = "Baixar e instalar";
            BotaoAdiarAtualizacao.IsEnabled = true;
        }
    }

    /// <summary>
    /// Esconde a faixa sem registrar recusa.
    ///
    /// Nao grava "dispensada": a verificacao e uma vez por dia, e no dia
    /// seguinte a faixa volta. Guardar a recusa para sempre faria uma correcao
    /// de seguranca ficar invisivel por causa de um clique distraido.
    /// </summary>
    private void AdiarAtualizacao_Click(object sender, RoutedEventArgs e) =>
        AvisoAtualizacao.Visibility = Visibility.Collapsed;

    private void AtualizarStatus(Snapshot s)
    {
        // Um recado recente sobre o plano de energia segura o rodape por alguns
        // segundos: senao a proxima leitura o apagaria antes de dar para ler.
        if (DateTime.UtcNow < _recadoAte) return;

        Status.Text = s.SensoresDeBaixoNivel
            ? "Sensores ativos · atualiza a cada 1 s"
            : $"{Diagnostico.ResumoCurto()} · atualiza a cada 1 s";
    }

    private static string Clock(float mhz) =>
        mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:F0} MHz";

    private static Brush Pincel(string chave) => (Brush)Application.Current.Resources[chave];

    /// <summary>Desenhos dos icones dos chips, num quadro de 24 por 24.</summary>
    private static class Icone
    {
        public const string Termometro = "M14,14.8 L14,4 A2,2 0 1,0 10,4 L10,14.8 A4,4 0 1,0 14,14.8 Z";
        public const string Memoria = "M3,8 L21,8 L21,16 L3,16 Z M7,16 L7,19 M12,16 L12,19 M17,16 L17,19";
        public const string Raio = "M13,2 L4,14 L11,14 L10,22 L19,10 L12,10 Z";
        public const string Relogio = "M12,3 A9,9 0 1,0 12,21 A9,9 0 1,0 12,3 Z M12,7 L12,12 L15,14";
        public const string Disco = "M3,6 L21,6 L21,18 L3,18 Z M7,14 L11,14";
        public const string Escudo = "M12,3 L20,6 L20,12 C20,17 16.5,20 12,21 C7.5,20 4,17 4,12 L4,6 Z";
    }

    /// <summary>Preenche os dois chips do cabecalho; sem desenho, o icone some.</summary>
    private void Chips(string? iconeEsq, string textoEsq, string? iconeDir, string textoDir)
    {
        Aplicar(IconeEsquerdo, iconeEsq);
        Aplicar(IconeDireito, iconeDir);
        ChipEsquerdo.Text = textoEsq;
        ChipDireito.Text = textoDir;

        static void Aplicar(Path alvo, string? desenho)
        {
            if (desenho is null)
            {
                alvo.Visibility = Visibility.Collapsed;
                return;
            }

            alvo.Data = Geometry.Parse(desenho);
            alvo.Visibility = Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------
    // Grade de nucleos

    /// <summary>
    /// A contagem vem do hardware, nunca fixa. Em "Núcleos" cada barra e a media
    /// das threads daquele nucleo, que e como a carga se distribui num CPU com
    /// duas threads por nucleo; em "Threads" cada barra e uma thread.
    /// </summary>
    private void MontarGrade(Snapshot s)
    {
        var threads = s.CpuCargaPorThread;
        var clocks = s.CpuClocksPorNucleo;

        var porNucleo = ModoNucleos.IsChecked == true;
        var podeAgrupar = clocks.Count > 0 && threads.Count == clocks.Count * 2;

        List<double> valores;
        string prefixo;

        if (porNucleo && podeAgrupar)
        {
            valores = new List<double>(clocks.Count);
            for (var i = 0; i < clocks.Count; i++)
                valores.Add((threads[i * 2] + threads[i * 2 + 1]) / 2.0);
            prefixo = "N";
        }
        else if (porNucleo && clocks.Count > 0 && threads.Count == clocks.Count)
        {
            // Sem SMT: thread e nucleo sao a mesma coisa.
            valores = threads.Select(v => (double)v).ToList();
            prefixo = "N";
        }
        else
        {
            valores = threads.Select(v => (double)v).ToList();
            prefixo = "T";
        }

        RotuloGrade.Text = prefixo == "N" ? "Uso por núcleo" : "Uso por thread";
        DetectadoCpu.Text = clocks.Count > 0
            ? $"detectado: {clocks.Count} núcleos · {threads.Count} threads"
            : $"detectado: {threads.Count} threads";

        // Sem agrupamento possivel, a opcao "Núcleos" nao teria o que mostrar.
        ModoNucleos.IsEnabled = clocks.Count > 0;

        SincronizarBarras(valores.Count);

        for (var i = 0; i < valores.Count; i++)
        {
            var linha = (BarraDeNucleo)GradeNucleos.Children[i];
            var clock = prefixo == "N" && i < clocks.Count ? clocks[i] : (float?)null;

            // O tooltip diz de onde veio o numero, para dar para conferir a
            // media contra o Gerenciador de Tarefas.
            var origem = prefixo == "N" && podeAgrupar
                ? $"média de T{i * 2 + 1} e T{i * 2 + 2}"
                : null;

            linha.Atualizar($"{prefixo}{i + 1}", valores[i], clock, origem);
        }
    }

    /// <summary>Cria ou remove barras so quando a contagem muda.</summary>
    private void SincronizarBarras(int quantidade)
    {
        while (GradeNucleos.Children.Count > quantidade)
            GradeNucleos.Children.RemoveAt(GradeNucleos.Children.Count - 1);

        while (GradeNucleos.Children.Count < quantidade)
            GradeNucleos.Children.Add(new BarraDeNucleo());

        // Sem folga entre as duas colunas a porcentagem de uma encosta no
        // rotulo da seguinte.
        for (var i = 0; i < GradeNucleos.Children.Count; i++)
        {
            var par = i % 2 == 0;
            ((BarraDeNucleo)GradeNucleos.Children[i]).Margin =
                new Thickness(0, 0, par ? 18 : 0, 8);
        }
    }

    // ------------------------------------------------------------------
    // Grafico do historico

    private void AreaGrafico_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistorico();

    private void AreaGraficoGpu_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistoricoGpu();

    private void AreaGraficoDisco_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistoricoDisco();

    private void AreaGraficoEnergia_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharHistoricoEnergia();

    private void DesenharHistorico() =>
        Desenhar(_historicoCpu, AreaGrafico, GraficoLinha, GraficoArea, CpuPicoHistorico);

    private void DesenharHistoricoGpu() =>
        Desenhar(_historicoGpu, AreaGraficoGpu, GraficoLinhaGpu, GraficoAreaGpu, GpuPicoHistorico);

    private void DesenharHistoricoDisco() =>
        Desenhar(_historicoDisco, AreaGraficoDisco, GraficoLinhaDisco, GraficoAreaDisco, DiscoPicoHistorico);

    private void DesenharHistoricoEnergia()
    {
        Desenhar(_historicoEnergia, AreaGraficoEnergia, GraficoLinhaEnergia, GraficoAreaEnergia,
                 EnergiaPicoHistorico);

        // O historico esta em porcentagem da fonte; o rotulo mostra watts.
        if (_historicoEnergia.Count >= 2)
            EnergiaPicoHistorico.Text =
                $"Pico: {_historicoEnergia.Max() / 100 * _cfg.CapacidadeFonteW:F0} W";
    }

    private static void Desenhar(Queue<double> historico, FrameworkElement area,
        Polyline linha, Polygon preenchimento, TextBlock rotuloDoPico)
    {
        var largura = area.ActualWidth;
        var altura = area.ActualHeight;

        if (largura <= 0 || altura <= 0 || historico.Count < 2)
        {
            linha.Points = new PointCollection();
            preenchimento.Points = new PointCollection();
            rotuloDoPico.Text = "";
            return;
        }

        var valores = historico.ToArray();

        // O grafico ocupa sempre a largura toda: com poucos pontos ele cresce da
        // esquerda para a direita conforme o historico enche.
        var passo = largura / Math.Max(valores.Length - 1, 1);

        var pontos = new PointCollection(valores.Length);
        for (var i = 0; i < valores.Length; i++)
        {
            var y = altura - valores[i] / 100.0 * (altura - 4) - 2;
            pontos.Add(new Point(i * passo, y));
        }

        linha.Points = pontos;

        // A area e a mesma linha fechada na base.
        preenchimento.Points = new PointCollection(pontos) { new(largura, altura), new(0, altura) };

        rotuloDoPico.Text = $"Pico: {valores.Max():F0}%";
    }

    // ------------------------------------------------------------------
    // Anel

    /// <summary>Arco do topo no sentido horario, proporcional a carga.</summary>
    private static Geometry? ArcoDeCarga(double porcentagem, double raio, double centro)
    {
        // Abaixo de uns 2% o arco vira um ponto solto no topo. Um minimo visual
        // mantem a leitura de "tem pouca carga" sem parecer defeito.
        var valor = Math.Clamp(porcentagem, 0, 100);
        if (valor <= 0) return null;
        valor = Math.Max(valor, 1.8);

        var angulo = Math.Min(valor, 99.999) / 100.0 * 2 * Math.PI;
        var meio = new Point(centro, centro);
        var inicio = new Point(meio.X, meio.Y - raio);
        var fim = new Point(
            meio.X + raio * Math.Sin(angulo),
            meio.Y - raio * Math.Cos(angulo));

        var arco = new ArcSegment(fim, new Size(raio, raio), 0,
            angulo > Math.PI, SweepDirection.Clockwise, true);

        return new PathGeometry(new[] { new PathFigure(inicio, new[] { arco }, false) });
    }

    // ------------------------------------------------------------------
    // Perfis de energia

    /// <summary>
    /// O plano pode ter sido trocado fora do app, entao o painel le o que
    /// esta em vigor em vez de assumir o que ele mesmo selecionou.
    /// </summary>
    private void SincronizarPerfil()
    {
        var atual = PowerService.Atual();

        _trocandoPerfil = true;
        PerfilEconomico.IsChecked = atual == PerfilEnergia.Economico;
        PerfilEquilibrado.IsChecked = atual == PerfilEnergia.Equilibrado;
        PerfilDesempenho.IsChecked = atual == PerfilEnergia.Desempenho;
        _trocandoPerfil = false;
    }

    private void Perfil_Click(object sender, RoutedEventArgs e)
    {
        if (_trocandoPerfil) return;

        var perfil = sender switch
        {
            var s when ReferenceEquals(s, PerfilEconomico) => PerfilEnergia.Economico,
            var s when ReferenceEquals(s, PerfilDesempenho) => PerfilEnergia.Desempenho,
            _ => PerfilEnergia.Equilibrado
        };

        // O Desempenho nao e so um plano de energia: abre a tela com as demais
        // acoes, para a pessoa escolher o que fazer.
        if (perfil == PerfilEnergia.Desempenho)
        {
            AbrirDesempenho();
            return;
        }

        if (PowerService.Aplicar(perfil))
        {
            Recado($"Plano de energia: {perfil}");
            return;
        }

        // Falhou: volta a marcacao para o plano que realmente esta ativo,
        // em vez de deixar o painel mostrando algo que nao vigora.
        Recado("Não foi possível trocar o plano de energia");
        SincronizarPerfil();
    }

    /// <summary>
    /// Mensagem curta no rodapé, por alguns segundos. Pública porque o App
    /// também precisa dizer coisas por aqui — o resultado de uma procura de
    /// atualização que o usuário pediu, por exemplo.
    /// </summary>
    public void Recado(string texto)
    {
        Status.Text = texto;
        _recadoAte = DateTime.UtcNow.AddSeconds(4);
    }

    // ------------------------------------------------------------------
    // Botoes

    private void Minimizar_Click(object sender, RoutedEventArgs e) => Esconder();

    private void Personalizar_Click(object sender, RoutedEventArgs e) => AbrirPersonalizacao();

    /// <summary>
    /// Publica porque a ilha do topo tambem abre esta tela — e, vindo dela,
    /// abre ja na secao da ilha.
    /// </summary>
    public void AbrirPersonalizacao(bool irParaIlha = false)
    {
        // A tela aplica cor e transparencia na hora, para dar para ver o efeito
        // enquanto se mexe nos controles.
        var tela = new PersonalizacaoWindow(_cfg, () =>
        {
            (Application.Current as App)?.AplicarAparencia();
            AplicarTransparencia();
        }, irParaIlha)
        { Owner = this };

        tela.ShowDialog();
    }

    private void Ajustes_Click(object sender, RoutedEventArgs e) => AbrirConfiguracao(false);

    private void Sensores_Click(object sender, RoutedEventArgs e) =>
        new SensoresWindow(_sensores) { Owner = this }.ShowDialog();

    private void Estresse_Click(object sender, RoutedEventArgs e) =>
        new EstresseWindow(_cfg, _sensores) { Owner = this }.ShowDialog();

    private void Componentes_Click(object sender, RoutedEventArgs e)
    {
        new ComponentesWindow(_cfg, _sensores) { Owner = this }.ShowDialog();

        // A estimativa muda o anel e a faixa: repinta sem esperar a leitura.
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    /// <summary>
    /// Em "Unidades" a linha nao tem processo, entao nao abre nada.
    /// </summary>
    private void Processo_Clicado(object sender, MouseButtonEventArgs e)
    {
        if (sender is not LinhaDeDisco { Pid: { } pid, NomeDoProcesso: { } nome }) return;

        new ArquivosWindow(pid, nome) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// Abre o registro de consumo no bloco de notas, ou no que o Windows tiver
    /// associado a .txt. Grava antes: senão o arquivo mostraria o estado de até
    /// trinta segundos atrás, ou nem existiria na primeira vez.
    /// </summary>
    private void RegistroDeConsumo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _sensores.Consumo.Gravar();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ConsumoService.Caminho,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Erro("Abrir registro de consumo", ex);
        }
    }

    /// <summary>
    /// O par do cartão de RAM: ali é quem ocupa a memória, aqui é quem ocupa a
    /// placa de vídeo. Os dois ficam lado a lado na aba Geral e abrem do mesmo
    /// jeito, porque são a mesma pergunta sobre peças diferentes.
    /// </summary>
    private void CardGpu_Click(object sender, RoutedEventArgs e) =>
        new GpuProcessosWindow { Owner = this }.ShowDialog();

    private void CardRam_Click(object sender, RoutedEventArgs e) =>
        new MemoriaWindow(_sensores) { Owner = this }.ShowDialog();

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        new LogDiscoWindow(_cfg, _sensores) { Owner = this }.ShowDialog();

        // O log usa a mesma medicao por processo; ao voltar, as amostras antigas
        // virariam um pico falso.
        _processos.Reiniciar();
    }

    /// <summary>Publica porque o botao Desempenho da ilha cai aqui.</summary>
    public void AbrirDesempenho()
    {
        new DesempenhoWindow { Owner = this }.ShowDialog();

        // O plano pode ter mudado la dentro.
        SincronizarPerfil();
    }

    /// <summary>Tambem usada na primeira execucao, pela janela principal.</summary>
    public void AbrirConfiguracao(bool primeiraVez)
    {
        var tela = new ConfiguracaoWindow(_cfg, _sensores, primeiraVez) { Owner = this };
        tela.ShowDialog();

        // Os limites mudam a cor do anel, entao vale repintar sem esperar a
        // proxima leitura.
        if (_sensores.Ultimo is { } ultimo) Aplicar(ultimo);
    }

    /// <summary>
    /// Encerra o app de vez: solta os sensores, tira o icone da bandeja e sai.
    /// Nao ha trabalho a perder, entao nao pede confirmacao.
    /// </summary>
    private void Encerrar_Click(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();
}
