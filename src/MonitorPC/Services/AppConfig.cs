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
    /// Buscar o catalogo de especificacoes atualizado. Um GET de poucos
    /// kilobytes no repositorio do app, no maximo uma vez por semana. Desligado,
    /// vale a copia que veio embutida.
    /// </summary>
    public bool AtualizarCatalogoOnline { get; set; } = true;

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
    public static string Caminho => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MonitorPC", "config.json");

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
