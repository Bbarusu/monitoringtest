using System.Diagnostics;

namespace MonitorPC.Services;

public enum PerfilEnergia
{
    Economico,
    Equilibrado,
    Desempenho
}

/// <summary>
/// Troca o plano de energia do Windows pelos perfis do painel.
/// Usa o powercfg, que e o caminho oficial e nao exige privilegio elevado
/// para ativar um plano ja existente.
/// </summary>
public static class PowerService
{
    // GUIDs dos planos padrao do Windows, iguais em todas as instalacoes.
    private const string GuidEconomico = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string GuidEquilibrado = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string GuidDesempenho = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static string Guid(PerfilEnergia perfil) => perfil switch
    {
        PerfilEnergia.Economico => GuidEconomico,
        PerfilEnergia.Desempenho => GuidDesempenho,
        _ => GuidEquilibrado
    };

    /// <summary>
    /// Aplica o perfil. Devolve falso quando nao foi possivel, caso em que o
    /// motivo vai para o log — o painel nao deve mostrar um estado que nao vigora.
    /// </summary>
    public static bool Aplicar(PerfilEnergia perfil)
    {
        var guid = Guid(perfil);

        if (Executar($"/setactive {guid}") is null) return true;

        // Em muitas instalacoes recentes o plano de alto desempenho vem oculto.
        // Duplicar o plano o torna disponivel, e so entao da para ativa-lo.
        var erroDuplicar = Executar($"/duplicatescheme {guid}");
        if (erroDuplicar is not null)
        {
            Log.Erro("Plano de energia", new InvalidOperationException(
                $"Não foi possível preparar o plano {perfil}: {erroDuplicar}"));
            return false;
        }

        var erro = Executar($"/setactive {guid}");
        if (erro is null) return true;

        Log.Erro("Plano de energia", new InvalidOperationException(
            $"Não foi possível ativar o plano {perfil}: {erro}"));
        return false;
    }

    /// <summary>Perfil em vigor agora, ou nulo se for um plano fora dos tres.</summary>
    public static PerfilEnergia? Atual()
    {
        try
        {
            var saida = Saida("/getactivescheme");
            if (saida is null) return null;

            if (saida.Contains(GuidEconomico, StringComparison.OrdinalIgnoreCase)) return PerfilEnergia.Economico;
            if (saida.Contains(GuidDesempenho, StringComparison.OrdinalIgnoreCase)) return PerfilEnergia.Desempenho;
            if (saida.Contains(GuidEquilibrado, StringComparison.OrdinalIgnoreCase)) return PerfilEnergia.Equilibrado;

            return null;
        }
        catch (Exception ex)
        {
            Log.Erro("Plano de energia", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>Devolve nulo em caso de sucesso, ou a mensagem de erro.</summary>
    private static string? Executar(string argumentos)
    {
        try
        {
            using var p = Abrir(argumentos);
            if (p is null) return "não foi possível iniciar o powercfg";

            var erro = p.StandardError.ReadToEnd().Trim();
            var saida = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);

            if (p.ExitCode == 0) return null;
            return string.IsNullOrWhiteSpace(erro) ? saida : erro;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? Saida(string argumentos)
    {
        using var p = Abrir(argumentos);
        if (p is null) return null;

        var saida = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return saida;
    }

    private static Process? Abrir(string argumentos) => Process.Start(new ProcessStartInfo
    {
        FileName = "powercfg",
        Arguments = argumentos,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    });
}
