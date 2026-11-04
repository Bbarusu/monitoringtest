using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorPC.Services;

namespace MonitorPC.Views;

/// <summary>
/// O guia da Kisland. Abre sozinho uma vez por versao do guia (veja
/// App.VersaoDoGuia), e fica
/// na Personalizacao para quem quiser rever.
///
/// Os controles sao de verdade: ligar, escolher o que aparece, como abrir e a
/// cor gravam na configuracao na hora, e a Kisland muda junto. Um guia que so
/// mostrasse deixaria a pessoa repetir tudo depois na Personalizacao.
/// </summary>
public partial class GuiaKislandWindow : Window
{
    private const int Passos = 10;

    /// <summary>A mesma paleta da Personalizacao.</summary>
    private static readonly (string Nome, string Hex)[] Paleta =
    {
        ("Azul", "#3B82F6"),
        ("Violeta", "#8B5CF6"),
        ("Turquesa", "#14B8A6"),
        ("Verde", "#22C55E"),
        ("Laranja", "#F97316"),
        ("Rosa", "#EC4899")
    };

    private readonly AppConfig _cfg;
    private readonly Action _aoMudar;
    private bool _carregando = true;
    private int _passo;

    /// <param name="obrigatorio">
    /// Aberto sozinho para todos (versao nova do guia): o "Pular" fica apagado,
    /// e o guia so fecha passando pelos passos. Aberto pela Personalizacao,
    /// para rever, o "Pular" funciona.
    /// </param>
    public GuiaKislandWindow(AppConfig cfg, Action aoMudar, bool obrigatorio = false)
    {
        InitializeComponent();

        if (obrigatorio)
        {
            BotaoPular.IsEnabled = false;
            BotaoPular.ToolTip = "Conheça a Kisland e o Kdrop até o fim para fechar";
        }
        // Abre no centro, mas com o mesmo cuidado das outras: numa tela baixa
        // (notebook com escala de 125%), o guia passava do fim da tela.
        Janelas.CaberNaTela(this);
        _cfg = cfg;
        _aoMudar = aoMudar;

        Ligar.IsChecked = _cfg.IlhaAtiva;
        ItemFps.IsChecked = _cfg.IlhaFps;

        // Sem o app elevado o contador nao roda: a chave fica apagada e diz
        // por que, como na Personalizacao.
        if (!AtualizacaoService.Elevado())
        {
            ItemFps.IsEnabled = false;
            DetalheFps.Text = "Precisa do KInfo instalado com acesso completo aos sensores.";
        }
        AbrirPassando.IsChecked = _cfg.IlhaAbrirAoPassarMouse;
        AbrirClicando.IsChecked = !_cfg.IlhaAbrirAoPassarMouse;
        ItemMusica.IsChecked = _cfg.IlhaMusica;
        ItemCpu.IsChecked = _cfg.IlhaCpu;
        ItemGpu.IsChecked = _cfg.IlhaGpu;
        ItemVolume.IsChecked = _cfg.IlhaVolume;
        ItemRam.IsChecked = _cfg.IlhaRam;
        ItemRelogio.IsChecked = _cfg.IlhaRelogio;
        ItemData.IsChecked = _cfg.IlhaData;
        ItemCalendario.IsChecked = _cfg.IlhaAbertaCalendario;
        ItemRoda.IsChecked = _cfg.IlhaRodaMudaVolume;
        ItemSaidas.IsChecked = _cfg.IlhaSaidasDeSom;
        ItemBandeja.IsChecked = _cfg.IlhaBandeja;
        ItemKdrop.IsChecked = _cfg.Kdrop && _cfg.IlhaBandeja;

        // O Kdrop abre uma porta na rede, e o Windows so deixa com o app
        // elevado: sem isso, a chave fica apagada e diz por que.
        if (!AtualizacaoService.Elevado())
        {
            ItemKdrop.IsEnabled = false;
            DetalheKdrop.Text = "Precisa do KInfo instalado com acesso completo (rodando como administrador).";
        }

        var agora = DateTime.Now;
        var hora = agora.ToString("HH:mm");
        PreviaHora.Text = hora;
        PreviaHora5.Text = hora;
        PreviaHoraAtalhos.Text = hora;
        PreviaData.Text = $"{SemanasCurtas[(int)agora.DayOfWeek]} {agora.Day}";

        MontarIas();
        MontarCalendario();

        MontarPaleta();
        MontarPontos();
        AtualizarPrevia();
        MostrarPasso(0);

        _carregando = false;
    }

    private void MontarPaleta()
    {
        foreach (var (nome, hex) in Paleta)
        {
            var botao = new RadioButton
            {
                Style = (Style)FindResource("Amostra"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!),
                Tag = hex,
                ToolTip = nome,
                GroupName = "CorDoGuia",

                // Acompanhando a cor do Windows, nenhuma aparece marcada: a
                // cor em uso nao e nenhuma das seis.
                IsChecked = !_cfg.AcompanharCorDoSistema &&
                            string.Equals(hex, _cfg.CorDestaque, StringComparison.OrdinalIgnoreCase)
            };
            System.Windows.Automation.AutomationProperties.SetName(botao, nome);
            botao.Checked += Cor_Escolhida;
            Cores.Children.Add(botao);
        }
    }

    private void MontarPontos()
    {
        for (var i = 0; i < Passos; i++)
            Pontos.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) });
    }

    // ------------------------------------------------------------------
    // Passos

    private void MostrarPasso(int passo)
    {
        _passo = Math.Clamp(passo, 0, Passos - 1);

        Passo1.Visibility = Visivel(_passo == 0);
        Passo2.Visibility = Visivel(_passo == 1);
        Passo3.Visibility = Visivel(_passo == 2);
        PassoCalendario.Visibility = Visivel(_passo == 3);
        PassoAtalhos.Visibility = Visivel(_passo == 4);
        PassoSom.Visibility = Visivel(_passo == 5);
        PassoKdrop.Visibility = Visivel(_passo == 6);
        Passo4.Visibility = Visivel(_passo == 7);
        PassoFps.Visibility = Visivel(_passo == 8);
        Passo5.Visibility = Visivel(_passo == 9);

        Contagem.Text = $"Conhecendo a Kisland · {_passo + 1} de {Passos}";
        BotaoVoltar.Visibility = _passo == 0 ? Visibility.Hidden : Visibility.Visible;
        BotaoAvancar.Content = _passo == Passos - 1 ? "Pronto!" : "Próximo";

        for (var i = 0; i < Pontos.Children.Count; i++)
        {
            if (Pontos.Children[i] is not Border ponto) continue;

            ponto.Width = i == _passo ? 18 : 6;
            ponto.SetResourceReference(Border.BackgroundProperty, i == _passo ? "Acento" : "Trilho");
        }
    }

    private static Visibility Visivel(bool sim) => sim ? Visibility.Visible : Visibility.Collapsed;

    private void Voltar_Click(object sender, RoutedEventArgs e) => MostrarPasso(_passo - 1);

    private void Avancar_Click(object sender, RoutedEventArgs e)
    {
        if (_passo == Passos - 1)
        {
            Close();
            return;
        }

        MostrarPasso(_passo + 1);
    }

    private void Pular_Click(object sender, RoutedEventArgs e)
    {
        if (BotaoPular.IsEnabled) Close();
    }

    private void Painel_Arrastar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject origem && DentroDeControle(origem)) return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private static bool DentroDeControle(DependencyObject? origem)
    {
        while (origem is not null)
        {
            if (origem is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (origem is not Visual) return false;
            origem = VisualTreeHelper.GetParent(origem);
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Opcoes, que valem na hora

    private void Opcao_Alterada(object sender, RoutedEventArgs e)
    {
        AtualizarPrevia();
        if (_carregando) return;

        // O Kdrop e a bandeja aberta para a casa: ligar um liga a outra, e
        // desligar a bandeja desliga ele. A troca dispara esta mesma funcao,
        // que entao grava tudo.
        if (ReferenceEquals(sender, ItemKdrop) && ItemKdrop.IsChecked == true && ItemBandeja.IsChecked != true)
        {
            ItemBandeja.IsChecked = true;
            return;
        }
        if (ReferenceEquals(sender, ItemBandeja) && ItemBandeja.IsChecked != true && ItemKdrop.IsChecked == true)
        {
            ItemKdrop.IsChecked = false;
            return;
        }

        _cfg.IlhaAtiva = Ligar.IsChecked == true;
        _cfg.IlhaAbrirAoPassarMouse = AbrirPassando.IsChecked == true;
        _cfg.IlhaMusica = ItemMusica.IsChecked == true;
        _cfg.IlhaCpu = ItemCpu.IsChecked == true;
        _cfg.IlhaGpu = ItemGpu.IsChecked == true;
        _cfg.IlhaVolume = ItemVolume.IsChecked == true;
        _cfg.IlhaRam = ItemRam.IsChecked == true;
        _cfg.IlhaRelogio = ItemRelogio.IsChecked == true;
        _cfg.IlhaFps = ItemFps.IsChecked == true;
        _cfg.IlhaData = ItemData.IsChecked == true;
        _cfg.IlhaAbertaCalendario = ItemCalendario.IsChecked == true;
        _cfg.IlhaRodaMudaVolume = ItemRoda.IsChecked == true;
        _cfg.IlhaSaidasDeSom = ItemSaidas.IsChecked == true;
        _cfg.IlhaBandeja = ItemBandeja.IsChecked == true;
        _cfg.Kdrop = ItemKdrop.IsChecked == true;

        Confirmar();
    }

    /// <summary>
    /// Escolher uma das seis e deixar de acompanhar a cor do Windows: e o que
    /// a Personalizacao faz, e o guia faz igual.
    /// </summary>
    private void Cor_Escolhida(object sender, RoutedEventArgs e)
    {
        if (_carregando || sender is not RadioButton { Tag: string hex }) return;

        _cfg.AcompanharCorDoSistema = false;
        _cfg.CorDestaque = hex;
        Confirmar();
    }

    private void Confirmar()
    {
        _cfg.Salvar();
        _aoMudar();
    }

    // ------------------------------------------------------------------
    // Calendario e chat de IA

    private static readonly string[] Meses =
    {
        "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO",
        "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"
    };
    private static readonly string[] Semanas =
    {
        "Domingo", "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado"
    };
    private static readonly string[] SemanasCurtas = { "Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb" };
    private static readonly string[] Iniciais = { "D", "S", "T", "Q", "Q", "S", "S" };

    /// <summary>
    /// A previa do passo do calendario: o mes de verdade, com hoje marcado,
    /// como na Kisland aberta, so que menor.
    /// </summary>
    private void MontarCalendario()
    {
        var hoje = DateTime.Today;
        PreviaMes.Text = $"{Meses[hoje.Month - 1]} {hoje.Year}";
        PreviaSemana.Text = Semanas[(int)hoje.DayOfWeek];
        PreviaDia.Text = hoje.Day.ToString();

        var grade = PreviaCalendario;
        var primeiro = new DateTime(hoje.Year, hoje.Month, 1);
        var vazios = (int)primeiro.DayOfWeek;
        var semanas = (vazios + DateTime.DaysInMonth(hoje.Year, hoje.Month) + 6) / 7;

        for (var c = 0; c < 7; c++)
            grade.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        for (var l = 0; l < semanas; l++)
            grade.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });

        for (var c = 0; c < 7; c++)
        {
            var inicial = new TextBlock
            {
                Text = Iniciais[c],
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            inicial.SetResourceReference(TextBlock.ForegroundProperty, "TextoFraco");
            Grid.SetColumn(inicial, c);
            grade.Children.Add(inicial);
        }

        var inicio = primeiro.AddDays(-vazios);
        for (var i = 0; i < semanas * 7; i++)
        {
            var dia = inicio.AddDays(i);
            var linha = i / 7 + 1;
            var coluna = i % 7;

            if (dia == hoje)
            {
                var marca = new System.Windows.Shapes.Ellipse { Width = 15, Height = 15 };
                marca.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Acento");
                Grid.SetRow(marca, linha);
                Grid.SetColumn(marca, coluna);
                grade.Children.Add(marca);
            }

            var numero = new TextBlock
            {
                Text = dia.Day.ToString(),
                FontSize = 9.5,
                FontWeight = dia == hoje ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (dia == hoje)
                numero.Foreground = Brushes.White;
            else
                numero.SetResourceReference(TextBlock.ForegroundProperty,
                    dia.Month == hoje.Month ? "Texto" : "TextoMuitoFraco");
            if (dia.Month != hoje.Month) numero.Opacity = 0.55;

            Grid.SetRow(numero, linha);
            Grid.SetColumn(numero, coluna);
            grade.Children.Add(numero);
        }
    }

    /// <summary>Id de cada linha do seletor de chat, na mesma ordem dos itens.</summary>
    private readonly List<string> _idsDeIa = new();

    /// <summary>
    /// Os chats conhecidos. "Outro" so aparece para quem ja tem um endereco
    /// proprio escolhido: digitar o endereco e coisa da Personalizacao.
    /// </summary>
    private void MontarIas()
    {
        EscolhaIa.Items.Add("Nenhum");
        _idsDeIa.Add("");

        foreach (var ia in AtalhoService.Ias)
        {
            EscolhaIa.Items.Add(ia.Nome);
            _idsDeIa.Add(ia.Id);
        }

        if (_cfg.IlhaIa == AtalhoService.IaOutra)
        {
            EscolhaIa.Items.Add("Outro");
            _idsDeIa.Add(AtalhoService.IaOutra);
        }

        EscolhaIa.SelectedIndex = Math.Max(0, _idsDeIa.IndexOf(_cfg.IlhaIa ?? ""));
    }

    private void Ia_Escolhida(object sender, SelectionChangedEventArgs e)
    {
        if (_carregando) return;

        var indice = EscolhaIa.SelectedIndex;
        var id = indice >= 0 && indice < _idsDeIa.Count ? _idsDeIa[indice] : "";

        _cfg.IlhaIa = id.Length == 0 ? null : id;
        Confirmar();
    }

    /// <summary>A pilula do passo 3 mostra o que esta marcado, e o anel quando nada esta.</summary>
    private void AtualizarPrevia()
    {
        if (PreviaMusica is null) return;

        PreviaMusica.Visibility = Visivel(ItemMusica.IsChecked == true);
        PreviaCpu.Visibility = Visivel(ItemCpu.IsChecked == true);
        PreviaGpu.Visibility = Visivel(ItemGpu.IsChecked == true);
        PreviaRam.Visibility = Visivel(ItemRam.IsChecked == true);
        PreviaVolume.Visibility = Visivel(ItemVolume.IsChecked == true);
        PreviaRelogio.Visibility = Visivel(ItemRelogio.IsChecked == true);

        var algum = ItemMusica.IsChecked == true || ItemCpu.IsChecked == true ||
                    ItemGpu.IsChecked == true || ItemRam.IsChecked == true ||
                    ItemVolume.IsChecked == true || ItemRelogio.IsChecked == true;

        // Como na Kisland: com algo a mostrar, o botao de Personalizacao no
        // fim; sem nada, so o anel.
        PreviaAnel.Visibility = Visivel(!algum);
        PreviaBotao.Visibility = Visivel(algum && _cfg.IlhaBotaoPersonalizacao);
    }
}
