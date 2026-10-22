using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
