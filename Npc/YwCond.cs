using System;
using System.Collections.Generic;

namespace Lycoris.Npc
{
    /// <summary>
    /// Minimal helper for the Yo-kai Watch "yw-cond" condition blobs that live (base64-encoded) inside
    /// npc_talk TALK_CONFIG (ConditionalCond / TrigCond) and inside trigger DATA_ITEM records.
    ///
    /// We never build a cond from scratch — the daily-fight compiler CLONES a vanilla NPC's cond blobs and
    /// only swaps the embedded resource ids (a flag id inside GetGlobalBitFlag, a trigger id inside
    /// RunTrigger). Those ids are stored as a 4-byte BIG-ENDIAN value (e.g. the RunTrigger blob for trigger
    /// 0x19B40A96 contains the bytes 19 B4 0A 96). Swapping them is a byte-exact find/replace, which keeps
    /// the surrounding opcode stream byte-valid without needing a real encoder/decoder.
    ///
    /// Reference tool: https://n123git.github.io/yw-cond/ (n123git) — used only to understand the layout.
    /// </summary>
    public static class YwCond
    {
        /// <summary>
        /// Replace every big-endian 4-byte occurrence of <paramref name="oldId"/> with <paramref name="newId"/>
        /// inside a base64-encoded cond blob and return the re-encoded base64. Returns the input unchanged if
        /// it is not valid base64 or the id does not appear. Ids are compared as raw 32-bit values.
        /// </summary>
        public static string RemapBase64(string base64, int oldId, int newId)
        {
            if (string.IsNullOrEmpty(base64) || oldId == newId) return base64;
            byte[] blob;
            try { blob = Convert.FromBase64String(base64); }
            catch { return base64; }
            return ReplaceIdBytes(blob, oldId, newId) ? Convert.ToBase64String(blob) : base64;
        }

        /// <summary>
        /// Apply a whole id remap (donor id -&gt; new id) to a base64 cond blob. Every mapped id found in the
        /// blob (big-endian) is swapped. Used when a blob may reference more than one id.
        /// </summary>
        public static string RemapBase64(string base64, IReadOnlyDictionary<int, int> idMap)
        {
            if (string.IsNullOrEmpty(base64) || idMap == null || idMap.Count == 0) return base64;
            byte[] blob;
            try { blob = Convert.FromBase64String(base64); }
            catch { return base64; }
            bool any = false;
            foreach (var kv in idMap)
                if (kv.Key != kv.Value && ReplaceIdBytes(blob, kv.Key, kv.Value)) any = true;
            return any ? Convert.ToBase64String(blob) : base64;
        }

        /// <summary>Read the big-endian 4-byte resource id a single-parameter cond references, or null.
        /// GetGlobalBitFlag / RunTrigger store it at offset 19 (after the fixed opcode preamble).</summary>
        public static int? ReadParamId(string base64, int offset = 19)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            byte[] blob;
            try { blob = Convert.FromBase64String(base64); }
            catch { return null; }
            if (offset < 0 || offset + 4 > blob.Length) return null;
            return BigEndian(blob, offset);
        }

        // Replace all big-endian 4-byte windows equal to oldId. Returns true if at least one was replaced.
        private static bool ReplaceIdBytes(byte[] blob, int oldId, int newId)
        {
            byte o0 = (byte)((oldId >> 24) & 0xFF), o1 = (byte)((oldId >> 16) & 0xFF),
                 o2 = (byte)((oldId >> 8) & 0xFF), o3 = (byte)(oldId & 0xFF);
            bool any = false;
            for (int i = 0; i + 4 <= blob.Length; i++)
            {
                if (blob[i] == o0 && blob[i + 1] == o1 && blob[i + 2] == o2 && blob[i + 3] == o3)
                {
                    blob[i] = (byte)((newId >> 24) & 0xFF);
                    blob[i + 1] = (byte)((newId >> 16) & 0xFF);
                    blob[i + 2] = (byte)((newId >> 8) & 0xFF);
                    blob[i + 3] = (byte)(newId & 0xFF);
                    any = true;
                    i += 3;
                }
            }
            return any;
        }

        private static int BigEndian(byte[] d, int o) =>
            unchecked((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    }
}
