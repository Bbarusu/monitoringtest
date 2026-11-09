using System.IO;

namespace MonitorPC.Services;

public static class Log
{
    public static string Caminho => Path.Combine(AppConfig.Pasta, "erro.log");

    public static void Nota(string texto)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            File.AppendAllText(Caminho,
                $"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · nota ====={Environment.NewLine}{texto}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Erro(string origem, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            File.AppendAllText(Caminho,
                $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {origem} =====
                {ex}

                """);
        }
        catch
        {
        }
    }
}
