using System;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>Bitwise float comparisons: "equal" in a determinism check means the same bits.</summary>
    public static class Bits
    {
        public static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        public static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
        public static bool Same(Vector3 a, Vector3 b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z);
        public static bool Same(Quaternion a, Quaternion b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z) && Same(a.w, b.w);

        public static float Distance(Vector3 a, Vector3 b)
        {
            var dx = (double)a.x - b.x; var dy = (double)a.y - b.y; var dz = (double)a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Angle between two rotations in degrees, sign-insensitive.</summary>
        public static float Angle(Quaternion a, Quaternion b)
        {
            var la = Math.Sqrt((double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w);
            var lb = Math.Sqrt((double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w);
            if (!(la > 0) || !(lb > 0)) return float.NaN;
            var dot = Math.Abs(((double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w) / (la * lb));
            return (float)(2.0 * Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI);
        }
    }
}
