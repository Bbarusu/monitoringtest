using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MonitorPC.Services;

public static class IconeDoWindows
{
    public static ImageSource? DoApp(string idDoApp, int tamanho = 64)
    {
        if (string.IsNullOrWhiteSpace(idDoApp)) return null;

        return NumaThreadSta(() => Ler(@"shell:AppsFolder\" + idDoApp, tamanho, SoIcone | MaiorServe), TimeSpan.FromSeconds(3));
    }

    public static ImageSource? Miniatura(string caminho, int tamanho = 96)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !System.IO.File.Exists(caminho)) return null;

        return NumaThreadSta(() => Ler(caminho, tamanho, SoMiniatura | MaiorServe), TimeSpan.FromSeconds(8));
    }

    private static ImageSource? NumaThreadSta(Func<ImageSource?> ler, TimeSpan prazo)
    {
        ImageSource? resultado = null;
        var thread = new Thread(() => resultado = ler()) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(prazo) ? resultado : null;
    }

    private static ImageSource? Ler(string nome, int tamanho, int opcoes)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(nome, IntPtr.Zero, ref iid, out var fabrica) != 0 ||
                fabrica is null)
                return null;

            try
            {
                if (fabrica.GetImage(new Tamanho { Largura = tamanho, Altura = tamanho },
                                     opcoes, out bitmap) != 0 || bitmap == IntPtr.Zero)
                    return null;
            }
            finally
            {
                Marshal.ReleaseComObject(fabrica);
            }

            return Copiar(bitmap);
        }
        catch (Exception ex)
        {
            Log.Erro("Ícone do app", ex);
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    private static BitmapSource? Copiar(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<InfoDoBitmap>(), out var info) == 0) return null;

        var largura = info.Largura;
        var altura = Math.Abs(info.Altura);
        if (largura <= 0 || altura <= 0) return null;

        var cabecalho = new CabecalhoDib
        {
            Tamanho = 40,
            Largura = largura,
            Altura = -altura,
            Planos = 1,
            Bits = 32
        };

        var pixels = new byte[largura * altura * 4];
        var tela = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(tela, bitmap, 0, (uint)altura, pixels, ref cabecalho, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, tela);
        }

        var temAlfa = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0) continue;
            temAlfa = true;
            break;
        }

        if (!temAlfa)
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

        var imagem = BitmapSource.Create(largura, altura, 96, 96, PixelFormats.Pbgra32, null, pixels, largura * 4);
        imagem.Freeze();
        return imagem;
    }

    private const int MaiorServe = 0x1;
    private const int SoIcone = 0x4;
    private const int SoMiniatura = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Tamanho
    {
        public int Largura;
        public int Altura;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoDoBitmap
    {
        public int Tipo;
        public int Largura;
        public int Altura;
        public int BytesPorLinha;
        public ushort Planos;
        public ushort BitsPorPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CabecalhoDib
    {
        public int Tamanho;
        public int Largura;
        public int Altura;
        public ushort Planos;
        public ushort Bits;
        public uint Compressao;
        public uint TamanhoDaImagem;
        public int PixelsPorMetroX;
        public int PixelsPorMetroY;
        public uint CoresUsadas;
        public uint CoresImportantes;
        public uint Cores0;
        public uint Cores1;
        public uint Cores2;
        public uint Cores3;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Tamanho tamanho, int opcoes, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string caminho, IntPtr contexto, ref Guid iid,
                                                          [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr objeto, int tamanho, out InfoDoBitmap info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint primeira, uint linhas, byte[] pixels,
                                        ref CabecalhoDib info, uint uso);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objeto);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr janela);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr janela, IntPtr dc);
}
