using System.IO;

namespace MonitorPC.Services;

/// <summary>
/// Registro de erros em arquivo. Um app de bandeja nao tem console, entao sem
/// isto uma excecao vira so uma caixa de erro do Windows sem informacao nenhuma.
/// </summary>
public static class Log
{
    /// <summary>Na mesma pasta da configuracao, que trata a renomeacao do app.</summary>
    public static string Caminho => Path.Combine(AppConfig.Pasta, "erro.log");

    /// <summary>
    /// Uma linha de registro sem excecao, para o que precisa ficar anotado mas
    /// nao e falha de codigo — como o caminho de um diagnostico gravado.
    /// </summary>
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
            // Falhar ao registrar nao pode gerar outro erro.
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
            // Falhar ao registrar o erro nao pode gerar outro erro.
        }
    }
}
