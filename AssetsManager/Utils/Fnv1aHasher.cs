
using System.Text;

namespace AssetsManager.Utils
{
    public static class Fnv1aHasher
    {
        private const uint FNV_PRIME = 0x01000193;
        private const uint FNV_OFFSET_BASIS = 0x811C9DC5;

        public static uint Hash(string text)
        {
            uint hash = FNV_OFFSET_BASIS;
            byte[] bytes = Encoding.ASCII.GetBytes(text.ToLower());

            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= FNV_PRIME;
            }

            return hash;
        }
    }
}
