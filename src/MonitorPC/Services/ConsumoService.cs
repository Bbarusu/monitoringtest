using System.Globalization;
using System.IO;
using System.Text;

namespace MonitorPC.Services;

public sealed class DiaDeConsumo
{
    public DateOnly Data { get; set; }

    public double Wh { get; set; }

    public double Segundos { get; set; }

    public double WattsPico { get; set; }

    public double WattsMedia => Segundos > 0 ? Wh * 3600 / Segundos : 0;
}

public sealed class ConsumoService
{
    private const string Separador = "--- dados abaixo desta linha, não edite ---";

    private readonly List<DiaDeConsumo> _dias = new();
    private bool _mudou;

    private readonly object _trava = new();

    public ConsumoService() => Ler();

    public static string Caminho => Path.Combine(
        Path.GetDirectoryName(AppConfig.Caminho)!, "consumo.txt");

    public double WhDeHoje
    {
        get { lock (_trava) return Dia(DateOnly.FromDateTime(DateTime.Now))?.Wh ?? 0; }
    }

    public double WhTotal
    {
        get { lock (_trava) return _dias.Sum(d => d.Wh); }
    }

    public void Somar(double watts, double segundos)
    {
        if (watts <= 0 || segundos <= 0) return;

        lock (_trava)
        {
            var hoje = DateOnly.FromDateTime(DateTime.Now);
            var dia = Dia(hoje);

            if (dia is null)
            {
                dia = new DiaDeConsumo { Data = hoje };
                _dias.Add(dia);
            }

            dia.Wh += watts * segundos / 3600.0;
            dia.Segundos += segundos;
            dia.WattsPico = Math.Max(dia.WattsPico, watts);

            _mudou = true;
        }
    }

    private DiaDeConsumo? Dia(DateOnly data) => _dias.FirstOrDefault(d => d.Data == data);

    public void Gravar()
    {
        try
        {
            string texto;

            lock (_trava)
            {
                if (!_mudou) return;
                texto = Montar();
                _mudou = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            File.WriteAllText(Caminho, texto, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Erro("Gravar consumo", ex);
        }
    }

    private string Montar()
    {
        var t = new StringBuilder();

        t.AppendLine("KInfo — consumo de energia");
        t.AppendLine("=====================");
        t.AppendLine();
        t.AppendLine("ESTIMATIVA, e só com o app aberto.");
        t.AppendLine();
        t.AppendLine("CPU e placa de vídeo são medidas por sensor. Pentes de RAM, discos,");
        t.AppendLine("ventoinhas, placa-mãe, LEDs e o que você informou em Componentes de");
        t.AppendLine("energia entram por estimativa, com os watts por unidade configurados lá.");
        t.AppendLine();
        t.AppendLine("O tempo com o app fechado não é contado, e não há como recuperá-lo");
        t.AppendLine("depois. Compare sempre o consumo com a coluna \"aberto\".");
        t.AppendLine();
        t.AppendLine("Os valores são o consumo dos componentes. Para estimar o que sai da");
        t.AppendLine("tomada, divida pela eficiência da fonte configurada no app.");
        t.AppendLine();
        t.AppendLine($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}.");
        t.AppendLine();
        t.AppendLine("  Data          kWh     Aberto     Média      Pico");
        t.AppendLine("  ----------  -------  ---------  --------  --------");

        foreach (var d in _dias.OrderByDescending(d => d.Data))
            t.AppendLine(
                $"  {d.Data:dd/MM/yyyy}  {d.Wh / 1000.0,7:0.000}  {Duracao(d.Segundos),9}  " +
                $"{d.WattsMedia,6:0} W  {d.WattsPico,6:0} W");

        t.AppendLine("  ----------  -------  ---------  --------  --------");
        t.AppendLine(
            $"  Total       {WhTotal / 1000.0,7:0.000}  {Duracao(_dias.Sum(d => d.Segundos)),9}");
        t.AppendLine();
        t.AppendLine(Separador);

        foreach (var d in _dias.OrderBy(d => d.Data))
            t.AppendLine(string.Join('|',
                d.Data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.Wh.ToString("R", CultureInfo.InvariantCulture),
                d.Segundos.ToString("R", CultureInfo.InvariantCulture),
                d.WattsPico.ToString("R", CultureInfo.InvariantCulture)));

        return t.ToString();
    }

    private static string Duracao(double segundos)
    {
        var t = TimeSpan.FromSeconds(segundos);
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    private void Ler()
    {
        try
        {
            if (!File.Exists(Caminho)) return;

            var linhas = File.ReadAllLines(Caminho, Encoding.UTF8);
            var inicio = Array.IndexOf(linhas, Separador);
            if (inicio < 0) return;

            for (var i = inicio + 1; i < linhas.Length; i++)
            {
                var campos = linhas[i].Split('|');
                if (campos.Length < 4) continue;

                if (!DateOnly.TryParseExact(campos[0], "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var data)) continue;

                if (!double.TryParse(campos[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var wh))
                    continue;

                double.TryParse(campos[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seg);
                double.TryParse(campos[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var pico);

                if (Dia(data) is not null) continue;

                _dias.Add(new DiaDeConsumo
                {
                    Data = data,
                    Wh = wh,
                    Segundos = seg,
                    WattsPico = pico
                });
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Ler consumo", ex);
        }
    }
}
