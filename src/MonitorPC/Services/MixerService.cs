using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorPC.Services;

public sealed record AppDeSom(string Chave, string Nome, string? Caminho, int Nivel, bool Mudo, bool Ativo, bool Sistema);

public static class MixerService
{
    public const string ChaveDoSistema = "sistema";

    private static Guid _contexto = Guid.NewGuid();

    public static IReadOnlyList<AppDeSom> Apps()
    {
        var porApp = new Dictionary<string, (string Nome, string? Caminho, float Nivel, bool Mudo, bool Ativo, bool Sistema)>();

        foreach (var (sessao, volume) in Sessoes())
        {
            try
            {
                sessao.GetState(out var estado);
                if (estado == SessaoExpirada) continue;

                var sistema = sessao.IsSystemSoundsSession() == 0;
                sessao.GetProcessId(out var pid);

                string chave, nome;
                string? caminho = null;

                if (sistema)
                {
                    chave = ChaveDoSistema;
                    nome = "Sons do sistema";
                }
                else
                {
                    caminho = CaminhoDoProcesso(pid);
                    if (caminho is null && !ProcessoVivo(pid)) continue;

                    chave = caminho?.ToLowerInvariant() ?? $"pid:{pid}";
                    nome = NomeDoApp(sessao, caminho, pid);
                }

                volume.GetMasterVolume(out var nivel);
                volume.GetMute(out var mudo);
                var ativo = estado == SessaoAtiva;

                if (porApp.TryGetValue(chave, out var ja))
                {
                    porApp[chave] = (ja.Nome, ja.Caminho,
                                     ativo && !ja.Ativo ? nivel : Math.Max(ja.Nivel, nivel),
                                     ja.Mudo && mudo, ja.Ativo || ativo, ja.Sistema);
                }
                else
                {
                    porApp[chave] = (nome, caminho, nivel, mudo, ativo, sistema);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
            }
        }

        return porApp
            .Select(p => new AppDeSom(p.Key, p.Value.Nome, p.Value.Caminho,
                                      (int)Math.Round(p.Value.Nivel * 100), p.Value.Mudo, p.Value.Ativo, p.Value.Sistema))
            .OrderBy(a => a.Sistema)
            .ThenByDescending(a => a.Ativo)
            .ThenBy(a => a.Nome, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static void Definir(string chave, int nivel)
    {
        var escalar = Math.Clamp(nivel, 0, 100) / 100f;
        Aplicar(chave, v => v.SetMasterVolume(escalar, ref _contexto));
    }

    public static void DefinirMudo(string chave, bool mudo) =>
        Aplicar(chave, v => v.SetMute(mudo, ref _contexto));

    private static void Aplicar(string chave, Action<ISimpleAudioVolume> acao)
    {
        foreach (var (sessao, volume) in Sessoes())
        {
            try
            {
                if (ChaveDe(sessao) == chave) acao(volume);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
            }
        }
    }

    private static string? ChaveDe(IAudioSessionControl2 sessao)
    {
        if (sessao.IsSystemSoundsSession() == 0) return ChaveDoSistema;
        sessao.GetProcessId(out var pid);
        return CaminhoDoProcesso(pid)?.ToLowerInvariant() ?? $"pid:{pid}";
    }

    private static List<(IAudioSessionControl2 Sessao, ISimpleAudioVolume Volume)> Sessoes()
    {
        var lista = new List<(IAudioSessionControl2, ISimpleAudioVolume)>();

        try
        {
            var enumerador = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerador.GetDefaultAudioEndpoint(0 , 1 , out var aparelho) != 0)
                return lista;

            var iid = typeof(IAudioSessionManager2).GUID;
            aparelho.Activate(ref iid, 23 , IntPtr.Zero, out var objeto);
            var gerente = (IAudioSessionManager2)objeto;

            if (gerente.GetSessionEnumerator(out var sessoes) != 0) return lista;
            sessoes.GetCount(out var quantas);

            for (var i = 0; i < quantas; i++)
            {
                try
                {
                    if (sessoes.GetSession(i, out var controle) != 0) continue;
                    if (controle is IAudioSessionControl2 sessao && controle is ISimpleAudioVolume volume)
                        lista.Add((sessao, volume));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Mixer: listar sessões", ex);
        }

        return lista;
    }

    private const int SessaoAtiva = 1;
    private const int SessaoExpirada = 2;

    private static string NomeDoApp(IAudioSessionControl2 sessao, string? caminho, uint pid)
    {
        try
        {
            sessao.GetDisplayName(out var declarado);
            if (!string.IsNullOrWhiteSpace(declarado) && !declarado.StartsWith('@'))
                return declarado.Trim();
        }
        catch (COMException)
        {
        }

        if (caminho is not null)
        {
            if (Nomes.TryGetValue(caminho, out var guardado)) return guardado;

            string nome;
            try
            {
                var descricao = FileVersionInfo.GetVersionInfo(caminho).FileDescription?.Trim();
                nome = !string.IsNullOrEmpty(descricao) && descricao.Length <= 40
                    ? descricao
                    : Path.GetFileNameWithoutExtension(caminho);
            }
            catch
            {
                nome = Path.GetFileNameWithoutExtension(caminho);
            }

            Nomes[caminho] = nome;
            return nome;
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "App";
        }
    }

    private static readonly Dictionary<string, string> Nomes = new(StringComparer.OrdinalIgnoreCase);

    private static string? CaminhoDoProcesso(uint pid)
    {
        if (pid == 0) return null;

        var processo = OpenProcess(ConsultaLimitada, false, pid);
        if (processo == IntPtr.Zero) return null;

        try
        {
            var texto = new StringBuilder(1024);
            var tamanho = texto.Capacity;
            return QueryFullProcessImageName(processo, 0, texto, ref tamanho) ? texto.ToString(0, tamanho) : null;
        }
        finally
        {
            CloseHandle(processo);
        }
    }

    private static bool ProcessoVivo(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private const uint ConsultaLimitada = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint acesso, [MarshalAs(UnmanagedType.Bool)] bool herdar, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr processo, int formato, StringBuilder caminho, ref int tamanho);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr objeto);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessao, int fluxos, out IntPtr controle);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessao, int fluxos, out IntPtr volume);

    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessoes);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int quantas);
    [PreserveSig] int GetSession(int indice, [MarshalAs(UnmanagedType.IUnknown)] out object sessao);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    void GetState(out int estado);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string nome);
    void SetDisplayName(IntPtr a, IntPtr b);
    void GetIconPath(IntPtr a);
    void SetIconPath(IntPtr a, IntPtr b);
    void GetGroupingParam(IntPtr a);
    void SetGroupingParam(IntPtr a, IntPtr b);
    void RegisterAudioSessionNotification(IntPtr a);
    void UnregisterAudioSessionNotification(IntPtr a);

    void GetSessionIdentifier(IntPtr a);
    void GetSessionInstanceIdentifier(IntPtr a);
    void GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float nivel, ref Guid contexto);
    void GetMasterVolume(out float nivel);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mudo, ref Guid contexto);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mudo);
}
