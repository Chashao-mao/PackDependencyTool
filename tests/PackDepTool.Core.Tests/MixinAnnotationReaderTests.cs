using PackDepTool.Core.Analysis;
using Xunit;

namespace PackDepTool.Core.Tests;

public sealed class MixinAnnotationReaderTests
{
    [Fact]
    public void ReadsTargetsAndClassValue()
    {
        var bytes = BuildMixinClass();

        var targets = MixinAnnotationReader.ReadTargets(bytes);

        Assert.Contains("net.minecraft.client.Minecraft", targets);
        Assert.Contains("net.minecraft.world.Level", targets);
    }

    [Fact]
    public void ReturnsEmptyForNonClassBytes()
    {
        Assert.Empty(MixinAnnotationReader.ReadTargets([1, 2, 3, 4]));
    }

    internal static byte[] BuildMixinClass()
    {
        var output = new List<byte>();
        U4(output, 0xCAFEBABE);
        U2(output, 0);
        U2(output, 52);
        U2(output, 13);
        Utf8(output, "org/spongepowered/asm/mixin/Mixin");
        Utf8(output, "Lorg/spongepowered/asm/mixin/Mixin;");
        Utf8(output, "targets");
        Utf8(output, "net.minecraft.client.Minecraft");
        Utf8(output, "RuntimeVisibleAnnotations");
        Utf8(output, "TestMixin");
        Class(output, 6);
        Utf8(output, "java/lang/Object");
        Class(output, 8);
        Utf8(output, "value");
        Utf8(output, "Lnet/minecraft/world/Level;");
        Class(output, 11);

        U2(output, 0x0021);
        U2(output, 7);
        U2(output, 9);
        U2(output, 0);
        U2(output, 0);
        U2(output, 0);

        var annotation = new List<byte>();
        U2(annotation, 1);
        U2(annotation, 2);
        U2(annotation, 2);
        U2(annotation, 3);
        annotation.Add((byte)'s');
        U2(annotation, 4);
        U2(annotation, 10);
        annotation.Add((byte)'c');
        U2(annotation, 12);

        U2(output, 1);
        U2(output, 5);
        U4(output, (uint)annotation.Count);
        output.AddRange(annotation);
        return output.ToArray();
    }

    private static void Utf8(List<byte> output, string value)
    {
        output.Add(1);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        U2(output, (ushort)bytes.Length);
        output.AddRange(bytes);
    }

    private static void Class(List<byte> output, ushort nameIndex)
    {
        output.Add(7);
        U2(output, nameIndex);
    }

    private static void U2(List<byte> output, ushort value)
    {
        output.Add((byte)(value >> 8));
        output.Add((byte)value);
    }

    private static void U4(List<byte> output, uint value)
    {
        output.Add((byte)(value >> 24));
        output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 8));
        output.Add((byte)value);
    }
}
