using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Generated test inputs only. No downloaded media or personal files.
internal static class PreviewFixtures
{
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory); using var png=new MemoryStream(); png.Write(new byte[] {137,80,78,71,13,10,26,10});
        void Chunk(string type,byte[] data) {
            var header=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header,data.Length); png.Write(header); var payload=Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); png.Write(payload);
            uint crc=0xffffffff; foreach(var value in payload) { crc^=value; for(var bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0); }
            BinaryPrimitives.WriteUInt32BigEndian(header,~crc); png.Write(header);
        }
        var dimensions=new byte[13]; BinaryPrimitives.WriteInt32BigEndian(dimensions,32); BinaryPrimitives.WriteInt32BigEndian(dimensions.AsSpan(4),16); dimensions[8]=8; dimensions[9]=2; Chunk("IHDR",dimensions);
        using var compressed=new MemoryStream(); using(var zlib=new ZLibStream(compressed,CompressionLevel.SmallestSize,true))
            for(var y=0;y<16;y++) { zlib.WriteByte(0); for(var x=0;x<32;x++)zlib.Write(new byte[] {255,0,0}); }
        Chunk("IDAT",compressed.ToArray()); Chunk("IEND",[]); File.WriteAllBytes(Path.Combine(directory,"fixture.png"),png.ToArray());
        var objects=new[] {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> /Contents 4 0 R >>", Stream("0 1 0 rg\n0 0 200 300 re f\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> /Contents 6 0 R >>", Stream("0 0 1 rg\n0 0 200 300 re f\n") };
        using var pdf=new MemoryStream(); void Text(string value)=>pdf.Write(Encoding.ASCII.GetBytes(value));
        Text("%PDF-1.4\n"); var offsets=new List<long>();
        for(var index=0;index<objects.Length;index++) { offsets.Add(pdf.Position); Text($"{index+1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref=pdf.Position; Text("xref\n0 7\n0000000000 65535 f \n"); foreach(var offset in offsets)Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllBytes(Path.Combine(directory,"fixture.pdf"),pdf.ToArray());
    }
    private static string Stream(string content)=>$"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream";
}
