namespace SceneBuilder.Core.Model
{
    public readonly record struct Vec3(float X, float Y, float Z)
    {
        public static readonly Vec3 Zero = new(0, 0, 0);
        public static readonly Vec3 One = new(1, 1, 1);
    }
}
