using System;

namespace SceneBuilder.Core.Model
{
    // Unity-ZXY intrinsic Euler<->Quaternion conversion (q = qY * qX * qZ).
    public static class Rotation
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        public static Quat EulerToQuat(Vec3 eulerDegrees) =>
            EulerToQuat(eulerDegrees.X, eulerDegrees.Y, eulerDegrees.Z);

        public static Quat EulerToQuat(float x, float y, float z)
        {
            double hx = x * DegToRad / 2.0;
            double hy = y * DegToRad / 2.0;
            double hz = z * DegToRad / 2.0;

            double cX = Math.Cos(hx), sX = Math.Sin(hx);
            double cY = Math.Cos(hy), sY = Math.Sin(hy);
            double cZ = Math.Cos(hz), sZ = Math.Sin(hz);

            double qx = cY * sX * cZ + sY * cX * sZ;
            double qy = sY * cX * cZ - cY * sX * sZ;
            double qz = cY * cX * sZ - sY * sX * cZ;
            double qw = cY * cX * cZ + sY * sX * sZ;

            return new Quat((float)qx, (float)qy, (float)qz, (float)qw);
        }

        public static Vec3 QuatToEuler(Quat q)
        {
            double x = q.X, y = q.Y, z = q.Z, w = q.W;

            double sinX = 2.0 * (w * x - y * z);
            sinX = Math.Clamp(sinX, -1.0, 1.0);
            double eulerX = Math.Asin(sinX);

            double eulerY;
            double eulerZ;

            if (Math.Abs(sinX) < 0.9999999)
            {
                eulerY = Math.Atan2(2.0 * (w * y + x * z), 1.0 - 2.0 * (x * x + y * y));
                eulerZ = Math.Atan2(2.0 * (w * z + x * y), 1.0 - 2.0 * (x * x + z * z));
            }
            else
            {
                eulerY = Math.Atan2(-2.0 * (x * z - w * y), 1.0 - 2.0 * (y * y + z * z));
                eulerZ = 0.0;
            }

            return new Vec3(
                (float)(eulerX * RadToDeg),
                (float)(eulerY * RadToDeg),
                (float)(eulerZ * RadToDeg));
        }
    }
}
