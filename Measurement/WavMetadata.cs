using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MeaSound
{
    /// <summary>
    /// Writes a WAV LIST INFO chunk (RIFF metadata) to an existing WAV file.
    /// Supported tags: INAM, ICMT, IART, ISRC, ICRD, ISFT.
    /// </summary>
    internal static class WavMetadata
    {
        /// <summary>
        /// Writes a LIST INFO chunk with the provided tags.
        /// </summary>
        /// <param name="filePath">WAV file path.</param>
        /// <param name="tags">Tag-to-value dictionary.</param>
        public static void WriteInfoChunk(string filePath, Dictionary<string, string> tags)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            if (tags == null || tags.Count == 0) return;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                var header = new byte[12];
                if (fs.Read(header, 0, 12) < 12) return;
                if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F') return;
                if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E') return;

                byte[] listData = BuildListInfoPayload(tags);

                fs.Seek(0, SeekOrigin.End);
                fs.Write(listData, 0, listData.Length);

                long newSize = fs.Length - 8;
                fs.Seek(4, SeekOrigin.Begin);
                byte[] sizeBytes = BitConverter.GetBytes((uint)newSize);
                fs.Write(sizeBytes, 0, 4);

                Debug.WriteLine($"[WavMetadata] INFO chunk zapsán do {Path.GetFileName(filePath)} ({listData.Length} B)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WavMetadata] Chyba zápisu metadat: {ex.Message}");
            }
        }

        private static byte[] BuildListInfoPayload(Dictionary<string, string> tags)
        {
            using var ms = new MemoryStream();

            byte[] listId = Encoding.ASCII.GetBytes("LIST");
            ms.Write(listId, 0, 4);
            ms.Write(new byte[4], 0, 4);
            byte[] infoId = Encoding.ASCII.GetBytes("INFO");
            ms.Write(infoId, 0, 4);

            foreach (var kv in tags)
            {
                string tagId = (kv.Key + "    ").Substring(0, 4);
                string value = kv.Value ?? string.Empty;

                byte[] valueBytes = Encoding.UTF8.GetBytes(value);
                int payloadLen = valueBytes.Length + 1;

                ms.Write(Encoding.ASCII.GetBytes(tagId), 0, 4);
                ms.Write(BitConverter.GetBytes((uint)payloadLen), 0, 4);
                ms.Write(valueBytes, 0, valueBytes.Length);
                ms.WriteByte(0);

                if (payloadLen % 2 != 0)
                    ms.WriteByte(0);
            }

            byte[] result = ms.ToArray();

            uint listPayloadSize = (uint)(result.Length - 8);
            byte[] sizePatch = BitConverter.GetBytes(listPayloadSize);
            Array.Copy(sizePatch, 0, result, 4, 4);

            return result;
        }
    }
}
