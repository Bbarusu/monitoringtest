using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace MonitorPC.Setup;

public sealed record OpcoesDeInstalacao(
    bool AcessoCompletoAosSensores,

    bool InstalarPawnIo,

    bool AtalhoNaAreaDeTrabalho,
    bool IniciarComWindows,
    bool ExcluirDoDefender);

public static class Instalacao
{
    public const string Nome = "KInfo";
    public const string NomeDaTarefa = "KInfo";
    private const string ChaveDeDesinstalacao =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KInfo";

    public static string PastaPadrao => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Nome);

    private static string Executavel(string pasta) => Path.Combine(pasta, "KInfo.exe");
    private static string Desinstalador(string pasta) => Path.Combine(pasta, "Desinstalar.exe");

    public static async Task<List<string>> Instalar(
        string pasta, OpcoesDeInstalacao opcoes, IProgress<string> aviso, bool atualizacao = false)
    {
        var ressalvas = new List<string>();

        await Task.Run(() =>
        {
            aviso.Report("Fechando o app…");
            FecharAppAberto();

            aviso.Report("Limpando a instalação antiga, se houver…");
            LimparNomeAntigo();

            aviso.Report("Copiando os arquivos…");
            Directory.CreateDirectory(pasta);
            ExtrairPayload(pasta);

            aviso.Report("Desinstalando…");
            if (!InstalarDesinstalador(pasta))
                ressalvas.Add("Não foi possível copiar o desinstalador. Para remover o app depois, rode este instalador de novo.");

            if (opcoes.ExcluirDoDefender)
            {
                aviso.Report("Pedindo exceção ao Windows Defender…");

                if (!ExcluirDoDefender(pasta))
                    ressalvas.Add("O Windows Defender não aceitou a exceção. Se o app parar de abrir, procure a biblioteca de sensores na Quarentena.");
            }

            if (opcoes.InstalarPawnIo)
            {
                var queixa = InstalarPawnIo(aviso);
                if (queixa is not null) ressalvas.Add(queixa);
            }

            if (opcoes.AcessoCompletoAosSensores)
            {
                aviso.Report("Registrando o acesso aos sensores…");
                CriarTarefa(pasta, opcoes.IniciarComWindows);
            }
            else
            {
                aviso.Report("Pulando a execução como administrador, como você escolheu…");
                Executar("schtasks.exe", $"/Delete /TN \"{NomeDaTarefa}\" /F");
            }

            aviso.Report("Criando os atalhos…");
            CriarAtalhos(pasta, opcoes);

            aviso.Report("Registrando no Windows…");
            RegistrarDesinstalacao(pasta, opcoes);

            if (!atualizacao) PedirParaAbrirOPainel();
        });

        return ressalvas;
    }

    private static bool InstalarDesinstalador(string pasta)
    {
        var origem = Environment.ProcessPath;
        var destino = Desinstalador(pasta);

        if (origem is null) return false;

        if (string.Equals(origem, destino, StringComparison.OrdinalIgnoreCase)) return true;

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            try
            {
                if (File.Exists(destino))
                {
                    var velho = $"{destino}.{DateTime.Now:HHmmssfff}.antigo";
                    File.Move(destino, velho);

                    try { File.Delete(velho); } catch { }
                }

                File.Copy(origem, destino);
                return true;
            }
            catch (Exception ex)
            {
                Anotar($"copiar o desinstalador, tentativa {tentativa}: {ex.Message}");
                Thread.Sleep(500);
            }
        }

        return false;
    }

    public static async Task Desinstalar(IProgress<string> aviso)
    {
        await Task.Run(() =>
        {
            var pasta = PastaInstalada() ?? PastaPadrao;

            aviso.Report("Fechando o app…");
            FecharAppAberto();

            aviso.Report("Removendo a tarefa dos sensores…");
            Executar("schtasks.exe", $"/Delete /TN \"{NomeDaTarefa}\" /F");

            aviso.Report("Removendo os atalhos…");
            foreach (var atalho in new[]
                     { AtalhoDoMenu(), AtalhoDaAreaDeTrabalho(), AtalhoDeInicializacao() })
                try { if (File.Exists(atalho)) File.Delete(atalho); } catch { }

            aviso.Report("Removendo a exceção do Defender…");
            PowerShell($"Remove-MpPreference -ExclusionPath '{pasta.Replace("'", "''")}'");

            aviso.Report("Apagando os arquivos…");
            ApagarPasta(pasta);

            try { Registry.LocalMachine.DeleteSubKeyTree(ChaveDeDesinstalacao, false); } catch { }
        });
    }

    private static void ExtrairPayload(string pasta)
    {
        using var fluxo = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip")
            ?? throw new InvalidOperationException(
                "Este instalador foi compilado sem o app dentro. Rode o publicar.cmd.");

        using var zip = new ZipArchive(fluxo, ZipArchiveMode.Read);
        zip.ExtractToDirectory(pasta, overwriteFiles: true);
    }

    private static void LimparNomeAntigo()
    {
        const string antigo = "Monitor de PC";

        Executar("schtasks.exe", $"/Delete /TN \"{antigo}\" /F");

        try { Registry.LocalMachine.DeleteSubKeyTree(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MonitorDePC", false); }
        catch { }

        foreach (var pasta in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup)
                 })
            try
            {
                var atalho = Path.Combine(pasta, $"{antigo}.lnk");
                if (File.Exists(atalho)) File.Delete(atalho);
            }
            catch { }

        try
        {
            var instalacao = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), antigo);

            if (Directory.Exists(instalacao)) Directory.Delete(instalacao, recursive: true);
        }
        catch { }
    }

    private static void FecharAppAberto()
    {
        foreach (var p in Process.GetProcessesByName("KInfo")
                     .Concat(Process.GetProcessesByName("MonitorPC")))
        {
            try
            {
                if (p.CloseMainWindow() && p.WaitForExit(4000)) continue;
                p.Kill();
                p.WaitForExit(4000);
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static void CriarTarefa(string pasta, bool noLogon)
    {
        var usuario = $"{Environment.UserDomainName}\\{Environment.UserName}";

        var gatilho = noLogon
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{Seguro(usuario)}</UserId>" +
              "<Delay>PT20S</Delay></LogonTrigger>"
            : "";

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Abre o {Nome} com acesso aos sensores de hardware.</Description>
              </RegistrationInfo>
              <Triggers>{gatilho}</Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Seguro(usuario)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Seguro(Executavel(pasta))}</Command>
                  <Arguments>--elevado</Arguments>
                  <WorkingDirectory>{Seguro(pasta)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var temporario = Path.Combine(Path.GetTempPath(), "monitorpc-tarefa.xml");
        File.WriteAllText(temporario, xml, Encoding.Unicode);

        try
        {
            var (codigo, saida) = Executar(
                "schtasks.exe", $"/Create /TN \"{NomeDaTarefa}\" /XML \"{temporario}\" /F");

            if (codigo != 0 || Executar("schtasks.exe", $"/Query /TN \"{NomeDaTarefa}\"").Codigo != 0)
                throw new InvalidOperationException(
                    "O Windows recusou a tarefa que dá acesso aos sensores.\n\n" +
                    (saida.Length > 0 ? saida + "\n\n" : "") +
                    $"O passo a passo está em {Registro}. Dá para instalar sem ela, " +
                    "desmarcando \"Rodar como administrador\" — o app funciona, " +
                    "mas sem temperatura de CPU, tensões e ventoinhas.");
        }
        finally
        {
            try { File.Delete(temporario); } catch { }
        }
    }

    private static string Seguro(string texto) => texto
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string AtalhoDoMenu() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        "Programs", $"{Nome}.lnk");

    private static string AtalhoDaAreaDeTrabalho() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Nome}.lnk");

    private static string AtalhoDeInicializacao() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"{Nome}.lnk");

    private static void CriarAtalhos(string pasta, OpcoesDeInstalacao opcoes)
    {
        CriarAtalho(AtalhoDoMenu(), Executavel(pasta), pasta);

        if (opcoes.AtalhoNaAreaDeTrabalho)
            CriarAtalho(AtalhoDaAreaDeTrabalho(), Executavel(pasta), pasta);

        try { if (File.Exists(AtalhoDeInicializacao())) File.Delete(AtalhoDeInicializacao()); }
        catch { }

        if (opcoes.IniciarComWindows && !opcoes.AcessoCompletoAosSensores)
            CriarAtalho(AtalhoDeInicializacao(), Executavel(pasta), pasta);
    }

    private static void PedirParaAbrirOPainel()
    {
        try
        {
            var pasta = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KInfo");

            Directory.CreateDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "abrir-apos-instalar"), "");
        }
        catch
        {
        }
    }

    private static void CriarAtalho(string atalho, string alvo, string pasta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(atalho)!);

        PowerShell(
            $"$a = (New-Object -ComObject WScript.Shell).CreateShortcut('{Aspas(atalho)}'); " +
            $"$a.TargetPath = '{Aspas(alvo)}'; " +
            $"$a.WorkingDirectory = '{Aspas(pasta)}'; " +
            $"$a.Description = '{Nome}'; " +
            "$a.Save()");
    }

    private static string Aspas(string caminho) => caminho.Replace("'", "''");

    private static bool ExcluirDoDefender(string pasta) =>
        PowerShell($"Add-MpPreference -ExclusionPath '{Aspas(pasta)}'") == 0;

    private static void RegistrarDesinstalacao(string pasta, OpcoesDeInstalacao opcoes)
    {
        using var chave = Registry.LocalMachine.CreateSubKey(ChaveDeDesinstalacao);
        if (chave is null) return;

        chave.SetValue("DisplayName", Nome);
        chave.SetValue("DisplayVersion", Comum.Versao.Texto);
        chave.SetValue("Publisher", "Projeto pessoal");
        chave.SetValue("InstallLocation", pasta);
        chave.SetValue("DisplayIcon", Executavel(pasta));
        chave.SetValue("UninstallString",
            $"\"{Desinstalador(pasta)}\" {App.ArgumentoDeDesinstalacao}");
        chave.SetValue("NoModify", 1, RegistryValueKind.DWord);
        chave.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        Gravar(chave, "OpcaoSensores", opcoes.AcessoCompletoAosSensores);
        Gravar(chave, "OpcaoPawnIo", opcoes.InstalarPawnIo);
        Gravar(chave, "OpcaoAtalho", opcoes.AtalhoNaAreaDeTrabalho);
        Gravar(chave, "OpcaoLogon", opcoes.IniciarComWindows);
        Gravar(chave, "OpcaoDefender", opcoes.ExcluirDoDefender);

        static void Gravar(RegistryKey chave, string nome, bool valor) =>
            chave.SetValue(nome, valor ? 1 : 0, RegistryValueKind.DWord);
    }

    public static string? VersaoInstalada()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveDeDesinstalacao);
            return chave?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            return null;
        }
    }

    public static OpcoesDeInstalacao? OpcoesAnteriores()
    {
        var pasta = PastaInstalada();
        if (string.IsNullOrEmpty(pasta)) return null;

        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveDeDesinstalacao);

            bool? Lido(string nome) => chave?.GetValue(nome) is int v ? v != 0 : null;

            var tarefa = Lido("OpcaoSensores")
                         ?? Executar("schtasks.exe", $"/Query /TN \"{NomeDaTarefa}\"").Codigo == 0;

            var logon = Lido("OpcaoLogon") ?? (tarefa
                ? Executar("schtasks.exe", $"/Query /TN \"{NomeDaTarefa}\" /XML").Saida.Contains("LogonTrigger")
                : File.Exists(AtalhoDeInicializacao()));

            var atalho = Lido("OpcaoAtalho") ?? File.Exists(AtalhoDaAreaDeTrabalho());

            var defender = Lido("OpcaoDefender") ?? PowerShell(
                $"if ((Get-MpPreference).ExclusionPath -contains '{Aspas(pasta)}') {{ exit 0 }} else {{ exit 1 }}") == 0;

            var pawnIo = Lido("OpcaoPawnIo") ?? false;

            return new OpcoesDeInstalacao(tarefa, pawnIo, atalho, logon, defender);
        }
        catch (Exception ex)
        {
            Anotar($"ler as opções anteriores: {ex.Message}");
            return null;
        }
    }

    public static bool DriverPresente() => PawnIoInstalado();

    public static string? PastaInstalada()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(ChaveDeDesinstalacao);
            return chave?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    private static void ApagarPasta(string pasta)
    {
        if (!Directory.Exists(pasta)) return;

        foreach (var arquivo in Directory.GetFiles(pasta, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(arquivo, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try { File.Delete(arquivo); } catch { }
        }

        try { Directory.Delete(pasta, recursive: true); } catch { }

        if (Directory.Exists(pasta))
            MarcarParaApagarNoBoot(pasta);
    }

    private static void MarcarParaApagarNoBoot(string pasta) => Executar("cmd.exe",
        $"/c start \"\" /min cmd.exe /c \"timeout /t 5 >nul & rd /s /q \"{pasta}\"\"");

    private static int PowerShell(string script) => Executar("powershell.exe",
        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"").Codigo;

    public static string Registro => Path.Combine(Path.GetTempPath(), "KInfo-instalacao.log");

    private const string UrlDoPawnIo =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    private static string? InstalarPawnIo(IProgress<string> aviso)
    {
        const string ComoResolver =
            "O app abre e funciona sem ele: placa de vídeo, discos, memória e uso de CPU continuam normais, mas temperatura de CPU e ventoinhas da placa-mãe ficam de fora até o PawnIO ser instalado. Baixe em pawnio.eu e reabra o app.";

        if (PawnIoInstalado())
        {
            Anotar("PawnIO já estava instalado; nada a fazer.");
            return null;
        }

        var destino = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");

        try
        {
            aviso.Report("Baixando o PawnIO…");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Nome}-Setup/1.0");

                var bytes = http.GetByteArrayAsync(UrlDoPawnIo).GetAwaiter().GetResult();
                File.WriteAllBytes(destino, bytes);

                Anotar($"PawnIO baixado: {bytes.Length} bytes em {destino}");
            }

            var assinante = Assinante(destino);

            if (assinante is null)
                return "O PawnIO foi baixado mas não está assinado, então não foi " +
                       "executado. " + ComoResolver;

            if (!assinante.Contains("namazso", StringComparison.OrdinalIgnoreCase))
                return $"O PawnIO foi baixado mas está assinado por \"{assinante}\", " +
                       "e não pelo autor do projeto, então não foi executado. " +
                       ComoResolver;

            Anotar($"PawnIO assinado por: {assinante}");

            aviso.Report("Instalando o PawnIO…");

            var (codigo, _) = Executar(destino, "-install -silent");

            if (PawnIoInstalado())
            {
                Anotar($"PawnIO instalado (código {codigo}).");
                return null;
            }

            return codigo == 0
                ? "O instalador do PawnIO rodou mas o driver não apareceu no " +
                  "sistema. " + ComoResolver
                : $"O instalador do PawnIO terminou com código {codigo} e o driver " +
                  "não apareceu no sistema. " + ComoResolver;
        }
        catch (Exception ex)
        {
            Anotar($"PawnIO: exceção: {ex.Message}");

            return "Não foi possível baixar o PawnIO — a máquina pode estar sem " +
                   "internet agora. " + ComoResolver;
        }
        finally
        {
            try { if (File.Exists(destino)) File.Delete(destino); } catch { }
        }
    }

    private static bool PawnIoInstalado()
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\PawnIO");

            return chave is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? Assinante(string arquivo)
    {
        try
        {
            using var certificado = X509Certificate.CreateFromSignedFile(arquivo);
            return certificado.Subject;
        }
        catch
        {
            return null;
        }
    }

    private static (int Codigo, string Saida) Executar(string programa, string argumentos)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = programa,
                Arguments = argumentos,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (p is null) return (-1, "não foi possível iniciar " + programa);

            var saida = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(30000);

            Anotar($"{programa} {argumentos}\n  código {p.ExitCode}" +
                   (saida.Length > 0 ? $"\n  {saida.Replace("\n", "\n  ")}" : ""));

            return (p.ExitCode, saida);
        }
        catch (Exception ex)
        {
            Anotar($"{programa} {argumentos}\n  exceção: {ex.Message}");
            return (-1, ex.Message);
        }
    }

    private static void Anotar(string texto)
    {
        try
        {
            File.AppendAllText(Registro, $"[{DateTime.Now:HH:mm:ss}] {texto}\n");
        }
        catch
        {
        }
    }
}
