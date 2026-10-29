using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorPC.Models;

namespace MonitorPC.Services;

/// <summary>
/// Configuracao do app. Nenhum valor de hardware fica fixo no codigo:
/// quantidades sao detectadas, e os campos "Manual" sobrepoem a deteccao
/// para o que o PC nao enxerga (ventoinhas em controladora externa, LEDs, bomba).
/// </summary>
public sealed class AppConfig
{
    /// <summary>Opacidade minima do painel. Abaixo disto o texto deixa de ser legivel.</summary>
    public const int OpacidadeMinima = 15;

    // --- Watts por unidade (estimativa, ajustavel) ---
    public double WattsPorModuloRam { get; set; } = 3.0;
    public double WattsPorDisco { get; set; } = 2.0;
    public double WattsPorVentoinha { get; set; } = 1.5;
    public double WattsPorLed { get; set; } = 1.5;
    public double WattsBombaWaterCooler { get; set; } = 6.0;
    public double WattsPlacaMae { get; set; } = 12.0;
    public double WattsOutros { get; set; } = 10.0;

    // --- Quantidades: null = detectar; valor = manual, sobrepoe a deteccao ---
    public int? ModulosRamManual { get; set; }
    public int? DiscosManual { get; set; }
    public int? VentoinhasManual { get; set; }

    // --- Nao detectaveis por nenhum sensor ---
    public int PecasComLed { get; set; }
    public bool TemWaterCooler { get; set; }

    /// <summary>
    /// Potencia maxima da placa de video, em watts. Zero deixa o app descobrir
    /// sozinho: ele guarda o maior valor que os sensores ja registraram, que vai
    /// se aproximando do teto real conforme a GPU e exigida.
    /// </summary>
    public int PotenciaMaximaGpuW { get; set; }

    /// <summary>
    /// Maior potencia de GPU ja medida, mantida entre sessoes. Ultimo recurso:
    /// so vale quando o modelo nao esta no catalogo e nada foi informado a mao.
    /// </summary>
    public double PotenciaGpuMedida { get; set; }

    /// <summary>Maior rotacao de ventoinha da GPU ja medida.</summary>
    public double RotacaoGpuMedida { get; set; }

    /// <summary>
    /// Rotacao maxima da ventoinha da GPU, em rpm. Zero deixa o app projetar
    /// pela curva rotacao x PWM, que e mais confiavel que uma tabela: a rotacao
    /// varia entre fabricantes da mesma placa.
    /// </summary>
    public int RotacaoMaximaGpuRpm { get; set; }

    /// <summary>
    /// Potencia maxima do processador, em watts. Zero usa o PPT ou o Maximum
    /// Turbo Power do catalogo, e na falta dele o maior valor ja medido.
    /// </summary>
    public int PotenciaMaximaCpuW { get; set; }

    /// <summary>Maior potencia de pacote da CPU ja medida.</summary>
    public double PotenciaCpuMedida { get; set; }

    /// <summary>
    /// Curva rotacao x PWM de cada ventoinha, pela chave "peca/sensor". E daqui
    /// que sai a rotacao maxima: nenhuma tabela sabe qual ventilador esta
    /// espetado na placa, mas os dois sensores juntos respondem.
    /// </summary>
    public Dictionary<string, CurvaDeVentoinha> CurvasDeVentoinha { get; set; } = new();

    /// <summary>
    /// Maior taxa de leitura e de escrita ja vista em cada unidade, em MB/s,
    /// pelo nome do disco.
    ///
    /// Aqui nao ha o que extrapolar nem tabela que ajude: o SMART nao publica a
    /// velocidade de projeto da unidade, e ela depende tambem da porta em que o
    /// disco esta. Entao vale a medicao, que ao menos para de reescalar a cada
    /// sessao — um disco que ja chegou a 3 GB/s mantem essa referencia.
    /// </summary>
    public Dictionary<string, double> LeituraMaximaPorDisco { get; set; } = new();
    public Dictionary<string, double> EscritaMaximaPorDisco { get; set; } = new();

    /// <summary>
    /// Mostrar uma barra por ventoinha da placa-mae na aba CPU. Desligado em
    /// placas com muitos conectores, onde a aba fica longa demais — a rotacao
    /// continua sendo lida, e aparece na tela de todos os sensores.
    /// </summary>
    public bool MostrarVentoinhasNaCpu { get; set; } = true;

    /// <summary>
    /// Ventoinhas que o usuario escolheu nao ver, pela chave "peca/sensor".
    ///
    /// Guardadas as ocultas, e nao as visiveis, de proposito: assim a lista
    /// vazia significa "todas", uma ventoinha nova aparece sozinha, e desmarcar
    /// todas continua querendo dizer nenhuma — o que uma lista de visiveis nao
    /// conseguiria distinguir de "ainda nao escolhi".
    /// </summary>
    public List<string> VentoinhasOcultas { get; set; } = new();

    /// <summary>
    /// Buscar o catalogo de especificacoes atualizado. Um GET de poucos
    /// kilobytes no repositorio do app, no maximo uma vez por semana. Desligado,
    /// vale a copia que veio embutida.
    /// </summary>
    public bool AtualizarCatalogoOnline { get; set; } = true;

    /// <summary>
    /// Procura versao nova do app, no maximo uma vez por dia.
    ///
    /// Procurar e so procurar: encontrar vira um aviso no painel, e instalar
    /// continua sendo decisao de quem esta na frente do PC — custa uma
    /// confirmacao do Windows e fecha o app no meio.
    /// </summary>
    public bool VerificarAtualizacoes { get; set; } = true;

    /// <summary>
    /// Quando a ultima procura aconteceu, para nao repetir a cada abertura.
    /// Um app que sobe junto com a sessao abriria dez vezes num dia de
    /// reinicios, e dez consultas para a mesma resposta nao servem a ninguem.
    /// </summary>
    public DateTime? UltimaVerificacaoDeAtualizacao { get; set; }

    // --- Fonte ---
    public double EficienciaFonte { get; set; } = 0.85;
    public int CapacidadeFonteW { get; set; } = 550;

    // --- Limites de temperatura (configuracao inicial) ---
    public double LimiteCpu { get; set; } = 75;
    public double LimiteGpu { get; set; } = 80;
    public double LimiteDisco { get; set; } = 55;
    public bool NotificarNoWindows { get; set; } = true;
    public bool SugerirEconomico { get; set; } = true;

    // --- Aparencia ---
    public Tema Tema { get; set; } = Tema.Escuro;
    public bool AcompanharCorDoSistema { get; set; } = true;
    public string CorDestaque { get; set; } = "#3B82F6";
    public bool Transparencia { get; set; } = true;
    public int Opacidade { get; set; } = 85;

    /// <summary>
    /// Opacidade da Kisland, separada da do painel. Vazia segue a do painel:
    /// e o que quem ja usava via, e continua vendo ate mexer na barra dela.
    /// </summary>
    public int? IlhaOpacidade { get; set; }

    // --- Ilha no topo da tela ---

    /// <summary>
    /// A pilula no centro do topo da tela. Desligada por padrao: uma janela
    /// sempre por cima de tudo nao pode aparecer sozinha depois de uma
    /// atualizacao — quem quiser liga em Personalizacao.
    /// </summary>
    public bool IlhaAtiva { get; set; }

    // O que a pilula mostra fechada. A musica so aparece quando ha algo tocando.
    public bool IlhaMusica { get; set; } = true;
    public bool IlhaCpu { get; set; } = true;
    public bool IlhaGpu { get; set; } = true;
    public bool IlhaVolume { get; set; } = true;
    public bool IlhaRam { get; set; }
    public bool IlhaCpuUso { get; set; }
    public bool IlhaGpuUso { get; set; }
    public bool IlhaDisco { get; set; }
    public bool IlhaPotencia { get; set; }
    public bool IlhaRelogio { get; set; }

    /// <summary>Dia da semana e do mes na pilula ("Qui 29"), junto do relogio ou sozinho.</summary>
    public bool IlhaData { get; set; }

    /// <summary>
    /// Chat de IA aberto pelo anel pulsante: o id de um dos chats conhecidos
    /// (AtalhoService.Ias), "outro" para um endereco proprio, ou vazio para
    /// nenhum. Com um escolhido, o anel fica sempre na pilula.
    /// </summary>
    public string? IlhaIa { get; set; }

    /// <summary>Endereco (ou app) do chat quando IlhaIa e "outro".</summary>
    public string? IlhaIaEndereco { get; set; }

    /// <summary>Apps e sites da Kisland aberta, na ordem em que aparecem.</summary>
    public List<AtalhoDaIlha> IlhaAtalhos { get; set; } = new();

    /// <summary>
    /// O convite para conhecer a Kisland ja apareceu. Aparece uma vez so, na
    /// primeira abertura de uma versao que tem o guia — para quem instalou
    /// agora e para quem atualizou. Depois, o guia fica na Personalizacao.
    /// </summary>
    public bool GuiaKislandVisto { get; set; }

    /// <summary>
    /// Contador de FPS na Kisland quando um jogo esta rodando, em qualquer
    /// monitor. Desligado por padrao: liga uma sessao de rastreamento do
    /// Windows, e quem nao joga nao precisa dela.
    /// </summary>
    public bool IlhaFps { get; set; }

    /// <summary>
    /// Tentar ficar visivel com o jogo em tela cheia "exclusiva". Muitos jogos
    /// que dizem isso rodam, na verdade, convertidos pelo Windows para sem
    /// borda, e ai a Kisland aparece. Nos de exclusiva de verdade ela nao
    /// aparece, e em alguns a tela pode piscar ou o jogo perder o foco quando
    /// ela tenta — por isso vem desligado.
    /// </summary>
    public bool IlhaFicarEmTelaCheia { get; set; }

    /// <summary>Tamanho da Kisland, em porcentagem do desenho original (70 a 100).</summary>
    public int IlhaEscala { get; set; } = 85;


    // O que a Kisland mostra aberta. O rodape fica sempre: e dele que saem o
    // painel e a Personalizacao.
    public bool IlhaAbertaPlayer { get; set; } = true;
    public bool IlhaAbertaTemperaturas { get; set; } = true;
    public bool IlhaAbertaSistema { get; set; } = true;
    public bool IlhaAbertaVolume { get; set; } = true;
    public bool IlhaAbertaPerfis { get; set; } = true;
    public bool IlhaAbertaCalendario { get; set; } = true;
    public bool IlhaAbertaAtalhos { get; set; } = true;

    /// <summary>Botao de Personalizacao na pilula. A ilha aberta tem o dela sempre.</summary>
    public bool IlhaBotaoPersonalizacao { get; set; } = true;

    /// <summary>
    /// Monitor da ilha, pelo nome do dispositivo do Windows ("\\.\DISPLAY2").
    /// Vazio e o principal. Monitor desligado faz a ilha ir para o principal
    /// sem apagar a escolha: religado, ela volta para ele.
    /// </summary>
    public string? IlhaMonitor { get; set; }

    /// <summary>Abre ao passar o mouse; falso, so ao clicar.</summary>
    public bool IlhaAbrirAoPassarMouse { get; set; } = true;

    /// <summary>Sai da frente de jogo, video e apresentacao.</summary>
    public bool IlhaEsconderEmTelaCheia { get; set; } = true;

    /// <summary>
    /// Mostra o volume na ilha por dois segundos quando ele muda. O aviso do
    /// proprio Windows continua: ele nao tem como ser desligado por outro app.
    /// </summary>
    public bool IlhaAvisoDeVolume { get; set; } = true;

    /// <summary>Mostra a faixa nova por tres segundos quando a musica muda.</summary>
    public bool IlhaAvisoDeFaixa { get; set; } = true;

    /// <summary>
    /// Fechar o painel ao clicar fora dele. Desligado por padrao: assim da para
    /// olhar o painel, trocar de janela e tirar print sem ele sumir. Quem fecha
    /// e o botao de minimizar ou o icone da bandeja.
    /// </summary>
    public bool FecharAoPerderFoco { get; set; }

    // --- Intervalos de leitura, em milissegundos ---
    public int IntervaloPainelAberto { get; set; } = 1000;
    public int IntervaloPainelFechado { get; set; } = 3000;
    public int IntervaloSensoresLentos { get; set; } = 30000;

    // --- Log de disco ---
    /// <summary>A partir de quantos MB/s um processo entra no log.</summary>
    public double LimiteLogMbs { get; set; } = 10;

    // --- Posicao do painel: vale so nesta sessao ---
    // Nao vai para o arquivo de proposito: cada abertura do app comeca no canto,
    // saindo da bandeja. Arrastar vale ate o app ser encerrado.

    /// <summary>
    /// Falso mantem o painel ancorado no canto inferior direito. Arrastar o
    /// painel liga isto e guarda onde ele ficou, ate encerrar.
    /// </summary>
    [JsonIgnore] public bool PosicaoLivre { get; set; }
    [JsonIgnore] public double PosicaoX { get; set; }
    [JsonIgnore] public double PosicaoY { get; set; }

    public bool ConfiguracaoInicialFeita { get; set; }

    // ------------------------------------------------------------------

    [JsonIgnore]
    public static string Caminho => Path.Combine(Pasta, "config.json");

    /// <summary>
    /// Onde moram a configuracao, o registro de consumo e o log de erros.
    ///
    /// O app se chamava Monitor de PC, e a pasta tinha o nome antigo. Quem ja
    /// usava tem ali curvas de ventoinha aprendidas durante dias e o consumo de
    /// cada dia: renomear sem levar isso junto jogaria fora o que o app mais
    /// demorou para saber. A mudanca acontece uma vez, na primeira abertura
    /// depois da renomeacao.
    /// </summary>
    [JsonIgnore]
    public static string Pasta { get; } = PrepararPasta();

    private static string PrepararPasta()
    {
        var dados = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var nova = Path.Combine(dados, "KInfo");

        try
        {
            var antiga = Path.Combine(dados, "MonitorPC");

            if (Directory.Exists(antiga) && !Directory.Exists(nova))
                Directory.Move(antiga, nova);
        }
        catch
        {
            // Sem Log aqui: o proprio Log depende desta pasta. Falhando, o app
            // comeca com configuracao nova, que e ruim mas nao impede nada.
        }

        return nova;
    }

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppConfig Carregar()
    {
        try
        {
            if (File.Exists(Caminho))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Caminho)) ?? new AppConfig();
        }
        catch
        {
            // Config corrompida nao pode impedir o app de abrir: volta ao padrao.
        }

        var cfg = new AppConfig();
        cfg.Salvar();
        return cfg;
    }

    public void Salvar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            File.WriteAllText(Caminho, JsonSerializer.Serialize(this, Opcoes));
        }
        catch
        {
            // Falha ao gravar nao derruba o app.
        }
    }
}
