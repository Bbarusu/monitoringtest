using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorPC.Services;

public static class OpenFilesService
{
    public static List<string> Listar(int pid, int limite = 200)
    {
        var achados = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var processo = AbrirProcesso(pid);
        if (processo == IntPtr.Zero) return new List<string>();

        try
        {
            foreach (var descritor in Descritores(pid))
            {
                if (achados.Count >= limite) break;

                var caminho = Caminho(processo, descritor);
                if (caminho is not null) achados.Add(caminho);
            }
        }
        catch (Exception ex)
        {
            Log.Erro("Arquivos abertos", ex);
        }
        finally
        {
            CloseHandle(processo);
        }

        return achados.ToList();
    }

    public static bool PodeInspecionar(int pid)
    {
        var h = AbrirProcesso(pid);
        if (h == IntPtr.Zero) return false;

        CloseHandle(h);
        return true;
    }

    private static IntPtr AbrirProcesso(int pid) =>
        OpenProcess(ProcessDupHandle | ProcessQueryLimited, false, pid);

    private static IEnumerable<ushort> Descritores(int pid)
    {
        var tamanho = 1 << 20;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(tamanho);
                var status = NtQuerySystemInformation(
                    SystemExtendedHandleInformation, buffer, tamanho, out _);

                if (status == 0) break;

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                if (status != unchecked((int)0xC0000004) || tamanho > 1 << 27)
                    yield break;

                tamanho *= 2;
            }

            var quantidade = Marshal.ReadIntPtr(buffer).ToInt64();
            var inicio = IntPtr.Size * 2;
            var passo = Marshal.SizeOf<EntradaDeDescritor>();

            for (long i = 0; i < quantidade; i++)
            {
                var endereco = IntPtr.Add(buffer, (int)(inicio + i * passo));
                var entrada = Marshal.PtrToStructure<EntradaDeDescritor>(endereco);

                if (entrada.ProcessoId.ToInt64() == pid)
                    yield return entrada.Descritor;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? Caminho(IntPtr processo, ushort descritor)
    {
        var copia = IntPtr.Zero;

        try
        {
            if (!DuplicateHandle(processo, descritor, EsteProcesso(), out copia,
                    0, false, DuplicateSameAccess))
                return null;

            if (GetFileType(copia) != FileTypeDisk) return null;

            var texto = new StringBuilder(1024);
            var tamanho = GetFinalPathNameByHandle(copia, texto, texto.Capacity, 0);

            if (tamanho == 0 || tamanho >= texto.Capacity) return null;

            var caminho = texto.ToString();

            if (caminho.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + caminho[8..];

            return caminho.StartsWith(@"\\?\", StringComparison.Ordinal) ? caminho[4..] : caminho;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (copia != IntPtr.Zero) CloseHandle(copia);
        }
    }

    private static IntPtr EsteProcesso() => Process.GetCurrentProcess().Handle;

    private const int SystemExtendedHandleInformation = 64;
    private const int ProcessDupHandle = 0x0040;
    private const int ProcessQueryLimited = 0x1000;
    private const int DuplicateSameAccess = 0x0002;
    private const uint FileTypeDisk = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct EntradaDeDescritor
    {
        public IntPtr Objeto;
        public IntPtr ProcessoId;
        public IntPtr DescritorCompleto;
        public uint Acesso;
        public ushort IndiceDePilha;
        public ushort IndiceDeTipo;
        public uint Atributos;
        public uint Reservado;

        public ushort Descritor => (ushort)DescritorCompleto.ToInt64();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int classe, IntPtr buffer, int tamanho, out int necessario);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int acesso, bool herdar, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr processoOrigem, ushort descritorOrigem, IntPtr processoDestino,
        out IntPtr descritorDestino, uint acesso, bool herdar, int opcoes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr descritor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr descritor);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetFinalPathNameByHandle(
        IntPtr descritor, StringBuilder caminho, int tamanho, int opcoes);
}
