using System.IO;

namespace MonitorPC.Services;

/// <summary>
/// Registro de erros em arquivo. Um app de bandeja nao tem console, entao sem
/// isto uma excecao vira so uma caixa de erro do Windows sem informacao nenhuma.
/// </summary>
public static class Log
{
    public static string Caminho => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MonitorPC", "erro.log");

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
            // Falhar ao registrar o erro nao pode gerar outro erro.
        }
    }
}
