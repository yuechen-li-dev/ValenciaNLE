using System.Text;

namespace Valencia.WhisperGpu;

internal sealed record Weight(string Name, int[] Dimensions, int Type, long Offset, int Elements);

/// <summary>Bounded reader for the pinned unquantized Whisper GGML files, not a general inference loader.</summary>
internal sealed class WhisperWeights
{
    private readonly string path;
    public int Width { get; }
    public int Heads { get; }
    public int Layers { get; }
    public int MelBands { get; }
    public float[] MelFilters { get; }
    public Dictionary<string, Weight> Tensors { get; } = new(StringComparer.Ordinal);
    public WhisperWeights(string path)
    {
        this.path = path;
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt32() != 0x67676d6c) throw new InvalidDataException("Expected Whisper GGML magic.");
        int[] header = Enumerable.Range(0, 11).Select(_ => reader.ReadInt32()).ToArray();
        Width = header[2]; Heads = header[3]; Layers = header[4]; MelBands = header[9];
        int bands = reader.ReadInt32(), frequencies = reader.ReadInt32();
        if (bands != MelBands || frequencies != 201) throw new InvalidDataException("Unexpected Whisper mel filters.");
        MelFilters = Enumerable.Range(0, bands * frequencies).Select(_ => reader.ReadSingle()).ToArray();
        int vocab = reader.ReadInt32();
        if (vocab < 1 || vocab > 100000) throw new InvalidDataException("Invalid vocabulary.");
        for (int i = 0; i < vocab; i++) { int length = reader.ReadInt32(); if (length < 0 || length > 65536) throw new InvalidDataException("Invalid token size."); file.Position += length; }
        while (file.Position < file.Length)
        {
            int dimensions = reader.ReadInt32(), nameLength = reader.ReadInt32(), type = reader.ReadInt32();
            if (dimensions is < 1 or > 4 || nameLength is < 1 or > 256 || type is not (0 or 1)) throw new InvalidDataException("Only pinned F32/F16 Whisper tensors are supported.");
            int[] shape = Enumerable.Range(0, dimensions).Select(_ => reader.ReadInt32()).ToArray();
            long elements = shape.Aggregate(1L, (n, d) => checked(n * d));
            string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            Tensors.Add(name, new(name, shape, type, file.Position, checked((int)elements)));
            file.Position += checked(elements * (type == 0 ? 4 : 2));
            if (file.Position > file.Length) throw new InvalidDataException("Truncated checkpoint tensor.");
        }
    }
    public float[] Read(string name)
    {
        var tensor = Tensors[name];
        if (tensor.Elements > 16_777_216) throw new NotSupportedException("Research fixture reads at most 64 MiB expanded per tensor.");
        using var file = File.OpenRead(path);
        file.Position = tensor.Offset;
        using var reader = new BinaryReader(file);
        var result = new float[tensor.Elements];
        for (int i = 0; i < result.Length; i++) result[i] = tensor.Type == 0 ? reader.ReadSingle() : (float)BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
        return result;
    }
}
