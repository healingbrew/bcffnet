using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BCFF
{
    public class BlockDecompressor : IDisposable
    {
        private Stream data;
        private Bitmap bitmap;
        public Bitmap Image => bitmap;
        private DDSHeader header;
        private DDSHeaderDXT10 header10;

        private unsafe struct DDSHeader
        {
            public int Magic;
            public int Size;
            public int Flags;
            public int Height;
            public int Width;
            public int PitchOrLinearSize;
            public int Depth;
            public int MipMapCount;
            public fixed int Reserved1[11];
            public DDSPixelFormat PixelFormat;
            public int Caps;
            public int Caps2;
            public int Caps3;
            public int Caps4;
            public int Reserved2;
        };
        private struct DDSPixelFormat
        {
            public int Size;
            public int Flags;
            public int FourCC;
            public int BitCount;
            public int RedMask;
            public int GreenMask;
            public int BlueMask;
            public int AlphaMask;
        }
        private struct DDSHeaderDXT10
        {
            public int Format;
            public int Dimension;
            public int Misc;
            public int Size;
            public int Misc2;
        }

        private const int FOURCC_DX10 = 808540228;
        private const int FOURCC_ATI1 = 826889281;
        private const int FOURCC_ATI2 = 843666497;
        private readonly static int[] DXGI_BC4 = { 79, 80, 91 };
        private readonly static int[] DXGI_BC5 = { 82, 83, 84 };

        private bool IsValid => DXGI_BC4.Contains(header10.Format) || DXGI_BC5.Contains(header10.Format) || header.PixelFormat.FourCC == FOURCC_ATI1 || header.PixelFormat.FourCC == FOURCC_ATI2;
        private bool IsBC4 => DXGI_BC4.Contains(header10.Format) || header.PixelFormat.FourCC == FOURCC_ATI1;

        private long start = 0;

        public BlockDecompressor(Stream input)
        {
            data = input;
            header = ReadStruct<DDSHeader>(input);
            if (header.PixelFormat.FourCC == FOURCC_DX10)
            {
                header10 = ReadStruct<DDSHeaderDXT10>(input);
            }

            if (!IsValid)
            {
                throw new InvalidDataException("Is not BC4 or BC5");
            }

            start = input.Position;
        }

        private float GetRed(int Red0, int Red1, byte Index)
        {
            if (Index == 0)
            {
                return Red0 / 255.0f;
            }
            if (Index == 1)
            {
                return Red1 / 255.0f;
            }
            float Red0f = Red0 / 255.0f;
            float Red1f = Red1 / 255.0f;
            if (Red0 > Red1)
            {
                Index -= 1;
                return (Red0f * (7 - Index) + Red1f * Index) / 7.0f;
            }
            else
            {
                if (Index == 6)
                {
                    return 0.0f;
                }
                if (Index == 7)
                {
                    return 1.0f;
                }
                Index -= 1;
                return (Red0f * (5 - Index) + Red1f * Index) / 5.0f;
            }
        }

        private byte GetIndex(ulong strip, int offset)
        {
            return (byte)((strip >> (3 * offset + 16)) & 0x7);
        }

        private float[] ReadIndice(BinaryReader reader)
        {
            int Red0;
            int Red1;

            Red0 = reader.ReadByte();
            Red1 = reader.ReadByte();

            reader.BaseStream.Position -= 2;
            ulong strip = reader.ReadUInt64(); // cause i'm lazy

            float[] block = new float[16];

            for (int i = 0; i < 16; ++i)
            {
                byte index = GetIndex(strip, i);
                block[i] = GetRed(Red0, Red1, index);
            }

            return block;
        }

        public static float[] VoidPass(float r, float g)
        {
            return new float[] { r, g, 0 };
        }

        public static float[] NormalMapPass(float x, float y)
        {
            float nx = 2 * x - 1;
            float ny = 2 * y - 1;
            float nz = 0.0f;
            if (1 - nx * nx - ny * ny > 0)
                nz = (float)Math.Sqrt(1 - nx * nx - ny * ny);
            float z = Math.Min(Math.Max((nz + 1) / 2.0f, 0.0f), 1.0f);
            return new float[] { x, y, z };
        }

        public void CreateImage()
        {
            CreateImage(VoidPass);
        }

        public void CreateImage(Func<float, float, float[]> pass)
        {
            if (!IsValid)
            {
                throw new InvalidDataException("Is not BC4 or BC5");
            }

            bitmap?.Dispose();
            bitmap = new Bitmap(header.Width, header.Height);
            bool IsBC5 = !IsBC4;

            using (BinaryReader reader = new BinaryReader(data, Encoding.Default, true))
            {
                data.Position = start;
                int x = 0;
                int y = 0;
                for (int i = 0; i < header.Width * header.Height; i += 16)
                {
                    float[] red = ReadIndice(reader);
                    float[] green = IsBC5 ? ReadIndice(reader) : null;
                    int sx = x;
                    int sy = y;
                    for (int j = 0; j < 16; ++j)
                    {
                        float[] color = pass(red[j], green?[j] ?? 0.0f);
                        int r = (int)Math.Floor(color[0] * 255.0f);
                        int g = (int)Math.Floor(color[1] * 255.0f);
                        int b = (int)Math.Floor(color[2] * 255.0f);
                        bitmap.SetPixel(x, y, Color.FromArgb(r, g, b));
                        x += 1;
                        if (x - sx >= 4)
                        {
                            x = sx;
                            y += 1;
                        }
                    }
                    x = sx + 4;
                    y = sy;
                    if (x >= header.Width)
                    {
                        x = 0;
                        y += 4;
                    }
                }
            }
        }

        private static T ReadStruct<T>(Stream data)
        {
            using (BinaryReader reader = new BinaryReader(data, Encoding.Default, true))
            {
                int size = Marshal.SizeOf<T>();
                byte[] buffer = reader.ReadBytes(size);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                Marshal.Copy(buffer, 0, ptr, size);
                T obj = Marshal.PtrToStructure<T>(ptr);
                Marshal.FreeHGlobal(ptr);
                return obj;
            }
        }

        public void Dispose()
        {
            bitmap?.Dispose();
            data?.Dispose();
        }

        public static void Main(string[] args)
        {
            using (Stream input = File.OpenRead(args[0]))
            {
                BlockDecompressor bcff = new BlockDecompressor(input);
                bcff.CreateImage(NormalMapPass);
                bcff.Image.Save(Path.ChangeExtension(args[0], "tif"), ImageFormat.Tiff);
            }
        }
    }
}
