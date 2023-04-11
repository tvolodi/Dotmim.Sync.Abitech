using System;
using System.Collections.Generic;
using System.Text;

namespace Dotmim.Sync
{
    public static class GuidArrConvertor
    {
        /// <summary>
        /// A CLSCompliant method to convert a big-endian Guid to little-endian
        /// and vice versa.
        /// The Guid Constructor (UInt32, UInt16, UInt16, Byte, Byte, Byte, Byte,
        ///  Byte, Byte, Byte, Byte) is not CLSCompliant.
        /// </summary>
        [CLSCompliant(true)]
        public static byte[] FlipEndian(this Guid guid)
        {
            var newBytes = new byte[16];
            var oldBytes = guid.ToByteArray();

            for (var i = 8; i < 16; i++)
                newBytes[i] = oldBytes[i];

            newBytes[3] = oldBytes[0];
            newBytes[2] = oldBytes[1];
            newBytes[1] = oldBytes[2];
            newBytes[0] = oldBytes[3];
            newBytes[5] = oldBytes[4];
            newBytes[4] = oldBytes[5];
            newBytes[6] = oldBytes[7];
            newBytes[7] = oldBytes[6];

            return newBytes;
        }
    }
}
